using Microsoft.Extensions.Options;

namespace SmartClassAC.Services;

public sealed class DeviceFingerprintService
{
    private readonly IUserAccountRepository _accounts;
    private readonly SchoolTimeOptions _schoolTime;

    public DeviceFingerprintService(IUserAccountRepository accounts, IOptions<SchoolTimeOptions> schoolTime)
    {
        _accounts = accounts;
        _schoolTime = schoolTime.Value;
    }

    public async Task<DeviceFingerprintEnrollResult> EnrollAsync(EnrollFingerprintRequest request, CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(request.DeviceCode, cancellationToken);
        if (device is null)
        {
            return new DeviceFingerprintEnrollResult(false, "Unknown or inactive biometric device.");
        }

        var result = await _accounts.EnrollFingerprintByPinAsync(
            request.PinCode,
            request.TemplateId,
            cancellationToken);

        if (!result.Ok)
        {
            return result;
        }

        return result with
        {
            ClassroomId = device.ClassroomId,
            ClassroomName = device.ClassroomName,
            DeviceCode = device.DeviceCode
        };
    }

    public async Task<DeviceFingerprintScanResult> ScanAsync(long templateId, string deviceCode, CancellationToken cancellationToken = default)
    {
        if (templateId <= 0)
        {
            return new DeviceFingerprintScanResult(false, "unknown", Message: "templateId must be a positive number.");
        }

        var device = await ResolveDeviceAsync(deviceCode, cancellationToken);
        if (device is null)
        {
            return new DeviceFingerprintScanResult(false, "unknown_device", Message: "Unknown or inactive biometric device.");
        }

        var schoolNow = SchoolClock.GetNow(_schoolTime);
        var schoolDate = DateOnly.FromDateTime(schoolNow);
        var schoolTime = TimeOnly.FromDateTime(schoolNow);
        try
        {
            await _accounts.ReconcileAttendanceOutcomesAsync(schoolDate, schoolTime, cancellationToken);
        }
        catch
        {
            // Do not block punch logging if reconciliation fails (e.g. pending migration).
        }

        var match = await _accounts.FindTeacherByFingerprintTemplateIdAsync(templateId, cancellationToken);
        if (match is null)
        {
            return new DeviceFingerprintScanResult(
                false,
                "unknown",
                ClassroomId: device.ClassroomId,
                ClassroomName: device.ClassroomName,
                DeviceCode: device.DeviceCode,
                Message: "No enrolled teacher matched that fingerprint ID.");
        }

        var activeClassroomId = await _accounts.GetActiveAttendanceClassroomIdAsync(match.TeacherAccountId, cancellationToken);
        if (activeClassroomId is long activeRoom)
        {
            if (activeRoom != device.ClassroomId)
            {
                return new DeviceFingerprintScanResult(
                    false,
                    "wrong_room",
                    match.TeacherAccountId,
                    match.DisplayName,
                    ClassroomId: device.ClassroomId,
                    ClassroomName: device.ClassroomName,
                    DeviceCode: device.DeviceCode,
                    Message: "Active class is in a different classroom. End it at that room's scanner.");
            }

            var ended = await _accounts.EndClassAsync(match.TeacherAccountId, cancellationToken);
            return ended.Succeeded
                ? new DeviceFingerprintScanResult(
                    true,
                    "ended",
                    match.TeacherAccountId,
                    match.DisplayName,
                    ClassroomId: device.ClassroomId,
                    ClassroomName: device.ClassroomName,
                    DeviceCode: device.DeviceCode)
                : new DeviceFingerprintScanResult(
                    false,
                    "error",
                    match.TeacherAccountId,
                    match.DisplayName,
                    ClassroomId: device.ClassroomId,
                    ClassroomName: device.ClassroomName,
                    DeviceCode: device.DeviceCode,
                    Message: ended.Error);
        }

        var scheduleId = await _accounts.FindInWindowScheduleIdAsync(
            match.TeacherAccountId,
            device.ClassroomId,
            schoolDate,
            schoolTime,
            cancellationToken);
        if (scheduleId is null)
        {
            return new DeviceFingerprintScanResult(
                false,
                "no_schedule",
                match.TeacherAccountId,
                match.DisplayName,
                ClassroomId: device.ClassroomId,
                ClassroomName: device.ClassroomName,
                DeviceCode: device.DeviceCode,
                Message: "No in-window schedule for this teacher in this classroom.");
        }

        var started = await _accounts.StartClassAsync(
            match.TeacherAccountId,
            scheduleId.Value,
            schoolDate,
            schoolTime,
            cancellationToken,
            "Biometric / fingerprint scanner");

        if (started.Succeeded)
        {
            return new DeviceFingerprintScanResult(
                true,
                "started",
                match.TeacherAccountId,
                match.DisplayName,
                scheduleId,
                device.ClassroomId,
                device.ClassroomName,
                device.DeviceCode);
        }

        // Attendance for this schedule already exists — allow AC-only re-entry while still in window.
        var reentry = await _accounts.TryInWindowAcReentryAsync(
            match.TeacherAccountId,
            scheduleId.Value,
            device.ClassroomId,
            schoolDate,
            schoolTime,
            cancellationToken);
        if (reentry is not null)
        {
            return new DeviceFingerprintScanResult(
                true,
                reentry.Action,
                match.TeacherAccountId,
                match.DisplayName,
                scheduleId,
                device.ClassroomId,
                device.ClassroomName,
                device.DeviceCode,
                reentry.Action == "ac_on" ? "AC resumed for this schedule window." : "AC turned off for this schedule window.");
        }

        return new DeviceFingerprintScanResult(
            false,
            "error",
            match.TeacherAccountId,
            match.DisplayName,
            scheduleId,
            device.ClassroomId,
            device.ClassroomName,
            device.DeviceCode,
            started.Error);
    }

    public async Task<DeviceRoomStatusResult> GetStatusAsync(string deviceCode, CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(deviceCode, cancellationToken);
        if (device is null)
        {
            return new DeviceRoomStatusResult(false, "Unknown or inactive biometric device.");
        }

        var schoolNow = SchoolClock.GetNow(_schoolTime);
        var schoolDate = DateOnly.FromDateTime(schoolNow);
        var schoolTime = TimeOnly.FromDateTime(schoolNow);
        try
        {
            await _accounts.ReconcileAttendanceOutcomesAsync(schoolDate, schoolTime, cancellationToken);
        }
        catch
        {
            // Still return best-effort status if reconciliation is unavailable.
        }

        return await _accounts.GetDeviceRoomStatusAsync(device.DeviceCode, schoolDate, schoolTime, cancellationToken);
    }

    private async Task<BiometricDeviceEntry?> ResolveDeviceAsync(string? deviceCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return null;
        }

        return await _accounts.FindBiometricDeviceByCodeAsync(deviceCode.Trim(), cancellationToken);
    }
}
