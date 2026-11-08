using Microsoft.Extensions.Options;

namespace SmartClassAC.Services;

public sealed class DeviceFingerprintService
{
    private readonly IUserAccountRepository _accounts;
    private readonly SchoolTimeOptions _schoolTime;
    private readonly PasswordHasher _passwordHasher;
    private readonly LoginAttemptLimiter _limiter;

    public DeviceFingerprintService(
        IUserAccountRepository accounts,
        IOptions<SchoolTimeOptions> schoolTime,
        PasswordHasher passwordHasher,
        LoginAttemptLimiter limiter)
    {
        _accounts = accounts;
        _schoolTime = schoolTime.Value;
        _passwordHasher = passwordHasher;
        _limiter = limiter;
    }

    public async Task<DeviceAdminResult> ListAssignableRoomsAsync(string? deviceId, string? adminPin, CancellationToken cancellationToken = default)
    {
        var (device, failure) = await AuthorizeAdminAsync(deviceId, adminPin, cancellationToken);
        if (device is null)
        {
            return failure!;
        }

        var rooms = await GetAssignableRoomsAsync(device, cancellationToken);
        return new DeviceAdminResult(true, Rooms: rooms.Select(room => new DeviceRoomOption(room.Id, room.Name, room.Id == device.ClassroomId)).ToList());
    }

    public async Task<DeviceAdminResult> AssignRoomAsync(string? deviceId, string? adminPin, long? classroomId, CancellationToken cancellationToken = default)
    {
        var (device, failure) = await AuthorizeAdminAsync(deviceId, adminPin, cancellationToken);
        if (device is null)
        {
            return failure!;
        }

        string? roomName = null;
        int? targetTemperature = null;
        if (classroomId is not null)
        {
            var room = (await GetAssignableRoomsAsync(device, cancellationToken)).FirstOrDefault(item => item.Id == classroomId);
            if (room is null)
            {
                return new DeviceAdminResult(false, StatusCodes.Status409Conflict, "That room is no longer available.");
            }

            roomName = room.Name;
            targetTemperature = room.TargetTemperature;
        }

        var result = await _accounts.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(
            device.Id,
            classroomId,
            targetTemperature,
            device.StatusPollingIntervalSeconds,
            device.IsActive,
            device.FingerprintEnrollmentEnabled,
            device.AttendanceScanEnabled), cancellationToken);

        return result.Succeeded
            ? new DeviceAdminResult(true, ClassroomName: roomName)
            : new DeviceAdminResult(false, StatusCodes.Status409Conflict, result.Error ?? "Assignment failed.");
    }

    public async Task<DeviceAdminResult> PairAsync(string? adminPin, string? pairingCode, string? clientKey, CancellationToken cancellationToken = default)
    {
        var limiterKeys = new[] { "device-pairing:global", $"device-pairing:{clientKey ?? "unknown"}" };
        if (limiterKeys.Any(_limiter.IsBlocked))
        {
            return new DeviceAdminResult(false, StatusCodes.Status429TooManyRequests, "Too many attempts. Try again later.");
        }

        var code = pairingCode?.Trim();
        var pairingValid = code is { Length: 6 } && code.All(char.IsAsciiDigit);
        if (!pairingValid || !await VerifyAdminPinAsync(adminPin, cancellationToken))
        {
            foreach (var key in limiterKeys)
            {
                _limiter.RecordFailure(key);
            }

            return new DeviceAdminResult(false, StatusCodes.Status401Unauthorized, "Invalid admin PIN or pairing code.");
        }

        var device = await _accounts.PairDeviceByCodeAsync(code!, cancellationToken);
        if (device is null)
        {
            foreach (var key in limiterKeys)
            {
                _limiter.RecordFailure(key);
            }

            return new DeviceAdminResult(false, StatusCodes.Status401Unauthorized, "Invalid admin PIN or pairing code.");
        }

        foreach (var key in limiterKeys)
        {
            _limiter.RecordSuccess(key);
        }

        return new DeviceAdminResult(true, DeviceId: device.DeviceId, DisplayName: device.DisplayName);
    }

    private async Task<bool> VerifyAdminPinAsync(string? adminPin, CancellationToken cancellationToken)
    {
        var pin = adminPin?.Trim();
        if (!AdminDevicePinCodes.IsValid(pin))
        {
            return false;
        }

        var valid = false;
        foreach (var hash in await _accounts.GetAdminDevicePinHashesAsync(cancellationToken))
        {
            valid |= _passwordHasher.Verify(pin!, hash);
        }

        return valid;
    }

    private async Task<(BiometricDeviceEntry? Device, DeviceAdminResult? Failure)> AuthorizeAdminAsync(string? deviceId, string? adminPin, CancellationToken cancellationToken)
    {
        var device = await ResolveDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return (null, new DeviceAdminResult(false, StatusCodes.Status404NotFound, "Unknown or inactive biometric device."));
        }

        var limiterKey = $"device-admin-pin:{device.DeviceId}";
        if (_limiter.IsBlocked(limiterKey))
        {
            return (null, new DeviceAdminResult(false, StatusCodes.Status429TooManyRequests, "Too many attempts. Try again later."));
        }

        var valid = await VerifyAdminPinAsync(adminPin, cancellationToken);

        if (!valid)
        {
            _limiter.RecordFailure(limiterKey);
            return (null, new DeviceAdminResult(false, StatusCodes.Status401Unauthorized, "Invalid admin PIN."));
        }

        _limiter.RecordSuccess(limiterKey);
        return (device, null);
    }

    private async Task<IReadOnlyList<ClassroomEntry>> GetAssignableRoomsAsync(BiometricDeviceEntry device, CancellationToken cancellationToken)
    {
        var rooms = await _accounts.GetClassroomsAsync(cancellationToken);
        var devices = await _accounts.GetBiometricDevicesAsync(cancellationToken);
        var taken = devices
            .Where(item => item.Id != device.Id && item.ClassroomId is not null)
            .Select(item => item.ClassroomId!.Value)
            .ToHashSet();
        return rooms.Where(room => room.IsActive && !taken.Contains(room.Id)).ToList();
    }

    public async Task<DeviceFingerprintEnrollResult> EnrollAsync(EnrollFingerprintRequest request, CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return new DeviceFingerprintEnrollResult(false, "Unknown or inactive biometric device.");
        }

        return new DeviceFingerprintEnrollResult(false, "Legacy fingerprint enrollment is unavailable until safe enrollment is implemented.");
    }

    public async Task<DeviceFingerprintScanResult> ScanAsync(long templateId, string deviceId, CancellationToken cancellationToken = default)
    {
        if (templateId <= 0)
        {
            return new DeviceFingerprintScanResult(false, "unknown", Message: "templateId must be a positive number.");
        }

        var device = await ResolveDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return new DeviceFingerprintScanResult(false, "unknown_device", Message: "Unknown or inactive biometric device.");
        }

        if (device.ClassroomId is null)
        {
            return new DeviceFingerprintScanResult(
                false,
                "unassigned",
                DeviceId: device.DeviceId,
                Message: "Assign this device to a classroom before scanning attendance.");
        }

        var classroomId = device.ClassroomId.Value;

        if (!device.AttendanceScanEnabled)
        {
            return new DeviceFingerprintScanResult(
                false,
                "disabled",
                ClassroomId: classroomId,
                ClassroomName: device.ClassroomName,
                DeviceId: device.DeviceId,
                Message: "Attendance scanning is disabled for this device.");
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

        var match = await _accounts.FindTeacherByFingerprintTemplateIdAsync(templateId, device.DeviceId, cancellationToken);
        if (match is null)
        {
            return new DeviceFingerprintScanResult(
                false,
                "unknown",
                ClassroomId: classroomId,
                ClassroomName: device.ClassroomName,
                DeviceId: device.DeviceId,
                Message: "No enrolled teacher matched that fingerprint ID.");
        }

        var activeClassroomId = await _accounts.GetActiveAttendanceClassroomIdAsync(match.TeacherAccountId, cancellationToken);
        if (activeClassroomId is long activeRoom)
        {
            if (activeRoom != classroomId)
            {
                return new DeviceFingerprintScanResult(
                    false,
                    "wrong_room",
                    match.TeacherAccountId,
                    match.DisplayName,
                    ClassroomId: classroomId,
                    ClassroomName: device.ClassroomName,
                    DeviceId: device.DeviceId,
                    Message: "Active class is in a different classroom. End it at that room's scanner.");
            }

            var ended = await _accounts.EndClassAsync(match.TeacherAccountId, cancellationToken);
            return ended.Succeeded
                ? new DeviceFingerprintScanResult(
                    true,
                    "ended",
                    match.TeacherAccountId,
                    match.DisplayName,
                    ClassroomId: classroomId,
                    ClassroomName: device.ClassroomName,
                    DeviceId: device.DeviceId)
                : new DeviceFingerprintScanResult(
                    false,
                    "error",
                    match.TeacherAccountId,
                    match.DisplayName,
                    ClassroomId: classroomId,
                    ClassroomName: device.ClassroomName,
                    DeviceId: device.DeviceId,
                    Message: ended.Error);
        }

        var scheduleId = await _accounts.FindInWindowScheduleIdAsync(
            match.TeacherAccountId,
            classroomId,
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
                ClassroomId: classroomId,
                ClassroomName: device.ClassroomName,
                DeviceId: device.DeviceId,
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
                classroomId,
                device.ClassroomName,
                device.DeviceId);
        }

        // Attendance for this schedule already exists — allow AC-only re-entry while still in window.
        var reentry = await _accounts.TryInWindowAcReentryAsync(
            match.TeacherAccountId,
            scheduleId.Value,
            classroomId,
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
                classroomId,
                device.ClassroomName,
                device.DeviceId,
                reentry.Action == "ac_on"
                    ? "Re-login: attendance reopened (time-out cleared)."
                    : "Logout: attendance time-out updated.");
        }

        return new DeviceFingerprintScanResult(
            false,
            "error",
            match.TeacherAccountId,
            match.DisplayName,
            scheduleId,
            device.ClassroomId,
            device.ClassroomName,
            device.DeviceId,
            started.Error);
    }

    public async Task<DeviceRoomStatusResult> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(deviceId, cancellationToken);
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

        return await _accounts.GetDeviceRoomStatusAsync(device.DeviceId, schoolDate, schoolTime, cancellationToken);
    }

    public async Task<OperationResult> ClearRoomSessionAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return OperationResult.Failure("Unknown or inactive biometric device.");
        }

        return await _accounts.ClearRoomSessionByDeviceIdAsync(device.DeviceId, cancellationToken);
    }

    private async Task<BiometricDeviceEntry?> ResolveDeviceAsync(string? deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return await _accounts.FindBiometricDeviceByIdAsync(deviceId.Trim(), cancellationToken);
    }
}
