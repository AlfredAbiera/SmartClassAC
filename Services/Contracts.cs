using System.Security.Cryptography;

namespace SmartClassAC.Services;

public enum UserRole { Admin, Teacher }

public sealed class DefaultAdminOptions
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = string.Empty;
}

public sealed class SchoolTimeOptions
{
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>Minutes after schedule start before a time-in is classified as Late.</summary>
    public int LateGraceMinutes { get; set; } = 15;

    /// <summary>Minutes after schedule end before an open session becomes MissingTimeOut.</summary>
    public int MissingTimeOutGraceMinutes { get; set; } = 15;
}

/// <summary>
/// School wall-clock time. User-visible timestamps are stored and shown as local school time
/// (not converted with ToLocalTime / UTC round-trips).
/// </summary>
public static class SchoolClock
{
    public static DateTime GetNow(SchoolTimeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TimeZoneId))
        {
            return DateTime.Now;
        }

        try
        {
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveTimeZone(options.TimeZoneId)).DateTime;
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTime.Now;
        }
        catch (InvalidTimeZoneException)
        {
            return DateTime.Now;
        }
    }

    public static DateOnly Today(SchoolTimeOptions options) => DateOnly.FromDateTime(GetNow(options));

    public static string Format(DateTime value, string format) => value.ToString(format);

    public static string Format(DateTime? value, string format, string fallback = "—") =>
        value is null ? fallback : value.Value.ToString(format);

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (timeZoneId.Contains('/'))
        {
            // Linux IANA id on Windows: try a common UTC+8 mapping used by this project.
            if (timeZoneId is "Asia/Manila" or "Asia/Singapore")
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");
            }

            throw;
        }
    }
}

public static class AttendanceOutcomes
{
    public const string OnTime = "OnTime";
    public const string Late = "Late";
    public const string Absent = "Absent";
    public const string MissingTimeOut = "MissingTimeOut";

    public static string Display(string? outcome, string status) =>
        status == "Active"
            ? outcome == Late ? "Active · Late" : "Active"
            : outcome switch
            {
                OnTime => "On time",
                Late => "Late",
                Absent => "Absent",
                MissingTimeOut => "Missing time-out",
                _ => status
            };

    public static string BadgeClass(string? outcome, string status) =>
        status == "Active"
            ? "pending"
            : outcome switch
            {
                OnTime => "active",
                Late => "pending",
                Absent => "rejected",
                MissingTimeOut => "rejected",
                _ => "neutral"
            };
}

public sealed class DemoDataOptions
{
    /// <summary>
    /// When true, seeds sample teachers/classrooms/schedules once (Development only recommended).
    /// </summary>
    public bool Enabled { get; set; }
}

public sealed record AuthenticatedUser(long Id, string Username, string DisplayName, string? Email, UserRole Role, bool IsDefaultAdmin);
public sealed record LoginResult(bool Succeeded, AuthenticatedUser? User, string? Error)
{
    public static LoginResult Failure(string error) => new(false, null, error);
    public static LoginResult Success(AuthenticatedUser user) => new(true, user, null);
}

public sealed record OperationResult(bool Succeeded, string? Error = null)
{
    public static OperationResult Success() => new(true);
    public static OperationResult Failure(string error) => new(false, error);
}

public sealed record CreateTeacherRequest(string DisplayName, string Username, string EmployeeNumber, string Email, string Password, string? DevicePin = null);
public sealed record UpdateAdminTeacherRequest(long AccountId, string DisplayName, string Username, string EmployeeNumber, string Email, string? DevicePin, string? NewPassword);
public sealed record TeacherDirectoryEntry(long Id, string DisplayName, string Username, string? EmployeeNumber, string Email, int FingerprintTemplateCount);
public sealed record ClassroomEntry(long Id, string Name, int Capacity, int TargetTemperature, decimal? CurrentTemperature, string AcStatus, bool IsActive);
public sealed record CreateClassroomRequest(string Name, int Capacity, int TargetTemperature);
public sealed record UpdateClassroomRequest(long Id, string Name, int Capacity, int TargetTemperature, bool IsActive);

public sealed record CreateScheduleRequest(
    long TeacherAccountId,
    long ClassroomId,
    long SemesterId,
    string ScheduleKind,
    string SubjectName,
    DateOnly ScheduleDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

public sealed record ScheduleDirectoryEntry(
    long Id,
    long TeacherAccountId,
    string TeacherDisplayName,
    long OriginalTeacherAccountId,
    string OriginalTeacherDisplayName,
    long ClassroomId,
    string ClassroomName,
    int ClassroomCapacity,
    long? SemesterId,
    string? SemesterName,
    string ScheduleKind,
    string SubjectName,
    DateOnly ScheduleDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

public sealed record SemesterEntry(long Id, string Name, DateOnly StartDate, DateOnly EndDate, bool IsActive);
public sealed record CreateSemesterRequest(string Name, DateOnly StartDate, DateOnly EndDate, bool IsActive = true);
public sealed record TeacherSemesterEntry(long SemesterId, string SemesterName, DateOnly SemesterStartDate, DateOnly SemesterEndDate, long TeacherAccountId, string TeacherDisplayName, string? EmployeeNumber);

public static class ScheduleKinds
{
    public const string Regular = "Regular";
    public const string Makeup = "Makeup";

    public static readonly string[] All = { Regular, Makeup };

    public static bool IsValid(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && All.Contains(kind, StringComparer.Ordinal);
}

public sealed record TeacherProfileEntry(long AccountId, string DisplayName, string Username, string? EmployeeNumber, string Email, string? Phone, string? DevicePin, int FingerprintTemplateCount);
public sealed record UpdateTeacherProfileRequest(string DisplayName, string Email, string? Phone, string? DevicePin, string? NewPassword);

public sealed record AttendanceLogEntry(
    long Id,
    long TeacherAccountId,
    string TeacherDisplayName,
    long ClassroomId,
    string ClassroomName,
    DateTime TimeIn,
    DateTime? TimeOut,
    string VerificationMethod,
    string Status,
    string? Outcome);

public sealed record TeacherRequestEntry(
    long Id,
    long TeacherAccountId,
    string TeacherDisplayName,
    string RequestType,
    DateOnly RequestDate,
    string Reason,
    bool NeedsSubstitute,
    long? SubstituteTeacherAccountId,
    string? SubstituteTeacherDisplayName,
    string Status,
    string? AdminResponse,
    DateTime CreatedAt,
    DateTime? ReviewedAt);

public sealed record SubmitTeacherRequest(string RequestType, DateOnly RequestDate, string Reason, bool NeedsSubstitute);
public sealed record ReviewTeacherRequest(long RequestId, string Status, long? SubstituteTeacherAccountId, string? AdminResponse);

public sealed record SupportTicketEntry(
    long Id,
    long TeacherAccountId,
    string TeacherDisplayName,
    string Category,
    string Details,
    string Status,
    string? AdminResponse,
    DateTime CreatedAt,
    DateTime? ResolvedAt);

public sealed record SubmitSupportTicket(string Category, string Details);
public sealed record ReviewSupportTicket(long TicketId, string Status, string? AdminResponse);

public sealed record TemperatureLogEntry(
    long Id,
    long ClassroomId,
    string ClassroomName,
    decimal? MeasuredTemperature,
    int? TargetTemperature,
    string EventType,
    string? Notes,
    DateTime RecordedAt);

public sealed record SetTemperatureRequest(long ClassroomId, int TargetTemperature, string EventType, string? Notes = null);

public sealed record PasswordResetDelivery(string Email, string RawToken);

public sealed record UserAccountRecord(long Id, string Username, string DisplayName, string? Email, string PasswordHash, UserRole Role, bool IsDefaultAdmin);

public sealed class DeviceApiOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public sealed record EnrollFingerprintRequest(string PinCode, long TemplateId, string DeviceCode);

public sealed record DeviceScanRequest(long TemplateId, string? DeviceCode);

public sealed record DeviceStatusRequest(string? DeviceCode);

public sealed record BiometricDeviceEntry(long Id, long ClassroomId, string ClassroomName, string DeviceCode, string DisplayName, bool IsActive);

public sealed record CreateBiometricDeviceRequest(long ClassroomId, string DeviceCode, string DisplayName);

public sealed record FingerprintTeacherMatch(long TeacherAccountId, string DisplayName, string Username);

/// <summary>
/// Room + AC + teacher snapshot for a biometric device (used after power-loss recovery).
/// </summary>
public sealed record DeviceRoomStatusResult(
    bool Ok,
    string? Error = null,
    string? DeviceCode = null,
    long? ClassroomId = null,
    string? ClassroomName = null,
    string? AcStatus = null,
    bool AcOn = false,
    bool SessionActive = false,
    long? TeacherAccountId = null,
    string? TeacherDisplayName = null,
    long? ScheduleId = null);

public sealed record DeviceFingerprintEnrollResult(
    bool Ok,
    string? Error = null,
    long? TeacherAccountId = null,
    string? DisplayName = null,
    string? Username = null,
    string? EmployeeNumber = null,
    string? DevicePin = null,
    string? Email = null,
    string? Phone = null,
    int? FingerprintTemplateCount = null,
    long? TemplateId = null,
    string? FingerPosition = null,
    long? ClassroomId = null,
    string? ClassroomName = null,
    string? DeviceCode = null);

public sealed record DeviceFingerprintScanResult(
    bool Ok,
    string Action,
    long? TeacherAccountId = null,
    string? TeacherDisplayName = null,
    long? ScheduleId = null,
    long? ClassroomId = null,
    string? ClassroomName = null,
    string? DeviceCode = null,
    string? Message = null);

/// <summary>
/// After a schedule’s attendance is already completed, in-window scans may toggle AC without a new attendance row.
/// </summary>
public sealed record AcReentryResult(string Action, string AcStatus);

public static class FingerprintSlots
{
    public static readonly string[] All =
    {
        "Right thumb", "Right index", "Right middle", "Right ring", "Right little",
        "Left thumb", "Left index", "Left middle", "Left ring", "Left little"
    };

    public static bool IsValid(string? fingerPosition) =>
        !string.IsNullOrWhiteSpace(fingerPosition)
        && All.Contains(fingerPosition, StringComparer.Ordinal);
}

public static class DevicePinCodes
{
    public const int MinLength = 4;
    public const int MaxLength = 6;
    public const string RequirementMessage = "PIN must be 4–6 digits and unique among teachers.";

    public static bool IsValid(string? pin) =>
        !string.IsNullOrWhiteSpace(pin)
        && pin.Length is >= MinLength and <= MaxLength
        && pin.All(char.IsDigit);

    public static string Normalize(string pin) => pin.Trim();

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt32(bytes) % 1_000_000;
        return value.ToString("D6");
    }
}

public interface IUserAccountRepository
{
    Task InitializeAsync(DefaultAdminOptions defaultAdmin, string passwordHash, CancellationToken cancellationToken);
    Task CheckHealthAsync(CancellationToken cancellationToken = default);
    Task<UserAccountRecord?> FindByIdentifierAsync(string usernameOrEmail, CancellationToken cancellationToken = default);
    Task<UserAccountRecord?> FindByIdAsync(long accountId, CancellationToken cancellationToken = default);
    Task<bool> IsInitialAdminSetupRequiredAsync(CancellationToken cancellationToken = default);
    Task<bool> SetInitialAdminEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateDefaultAdminCredentialsAsync(long accountId, string email, string? passwordHash, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateTeacherAsync(CreateTeacherRequest request, string passwordHash, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateAdminTeacherAsync(UpdateAdminTeacherRequest request, string? passwordHash, CancellationToken cancellationToken = default);
    Task<OperationResult> SetTeacherDevicePinByEmployeeNumberAsync(string employeeNumber, string pinCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeacherDirectoryEntry>> GetTeachersAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteTeacherAsync(long accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomEntry>> GetClassroomsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> CreateClassroomAsync(CreateClassroomRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateClassroomAsync(UpdateClassroomRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteClassroomAsync(long classroomId, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateScheduleAsync(CreateScheduleRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduleDirectoryEntry>> GetSchedulesAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteScheduleAsync(long scheduleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SemesterEntry>> GetSemestersAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> CreateSemesterAsync(CreateSemesterRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> SetSemesterActiveAsync(long semesterId, bool isActive, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeacherSemesterEntry>> GetTeacherSemestersAsync(long? semesterId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> EnrollTeacherInSemesterAsync(long teacherAccountId, long semesterId, CancellationToken cancellationToken = default);
    Task<OperationResult> UnenrollTeacherFromSemesterAsync(long teacherAccountId, long semesterId, CancellationToken cancellationToken = default);
    Task<PasswordResetDelivery?> CreatePasswordResetAsync(string email, DateTimeOffset expiresUtc, CancellationToken cancellationToken = default);
    Task<bool> ResetPasswordAsync(string rawToken, string passwordHash, CancellationToken cancellationToken = default);
    Task<TeacherProfileEntry?> GetTeacherProfileAsync(long accountId, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateTeacherProfileAsync(long accountId, UpdateTeacherProfileRequest request, string? passwordHash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduleDirectoryEntry>> GetTeacherSchedulesAsync(long accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AttendanceLogEntry>> GetAttendanceLogsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> StartClassAsync(long teacherAccountId, long scheduleId, DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default, string verificationMethod = "Biometric / dashboard verification");
    Task<OperationResult> EndClassAsync(long teacherAccountId, CancellationToken cancellationToken = default);
    Task<int> ReconcileAttendanceOutcomesAsync(DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeacherRequestEntry>> GetTeacherRequestsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> SubmitTeacherRequestAsync(long teacherAccountId, SubmitTeacherRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> ReviewTeacherRequestAsync(long adminAccountId, ReviewTeacherRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupportTicketEntry>> GetSupportTicketsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> SubmitSupportTicketAsync(long teacherAccountId, SubmitSupportTicket request, CancellationToken cancellationToken = default);
    Task<OperationResult> ReviewSupportTicketAsync(long adminAccountId, ReviewSupportTicket request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TemperatureLogEntry>> GetTemperatureLogsAsync(long? classroomId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> SetTemperatureAsync(long actorAccountId, SetTemperatureRequest request, CancellationToken cancellationToken = default);
    Task ResetSystemToFirstAccessAsync(DefaultAdminOptions defaultAdmin, string passwordHash, CancellationToken cancellationToken = default);
    Task<OperationResult> EnrollFingerprintAsync(long teacherAccountId, string fingerPosition, long templateId, CancellationToken cancellationToken = default);
    Task<DeviceFingerprintEnrollResult> EnrollFingerprintByPinAsync(string pinCode, long templateId, CancellationToken cancellationToken = default);
    Task<TeacherProfileEntry?> FindTeacherProfileByPinAsync(string pinCode, CancellationToken cancellationToken = default);
    Task<FingerprintTeacherMatch?> FindTeacherByFingerprintTemplateIdAsync(long templateId, CancellationToken cancellationToken = default);
    Task<BiometricDeviceEntry?> FindBiometricDeviceByCodeAsync(string deviceCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BiometricDeviceEntry>> GetBiometricDevicesAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> CreateBiometricDeviceAsync(CreateBiometricDeviceRequest request, CancellationToken cancellationToken = default);
    Task<DeviceRoomStatusResult> GetDeviceRoomStatusAsync(string deviceCode, DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default);
    /// <summary>
    /// Ends any active attendance in this device's classroom and sets AC Off (e.g. after sensor DELETEALL).
    /// </summary>
    Task<OperationResult> ClearRoomSessionByDeviceCodeAsync(string deviceCode, CancellationToken cancellationToken = default);
    Task<bool> HasActiveAttendanceAsync(long teacherAccountId, CancellationToken cancellationToken = default);
    Task<long?> GetActiveAttendanceClassroomIdAsync(long teacherAccountId, CancellationToken cancellationToken = default);
    Task<long?> FindInWindowScheduleIdAsync(long teacherAccountId, long classroomId, DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default);
    /// <summary>
    /// When attendance for this schedule already exists and the window is still open,
    /// re-login clears time-out (Active + AC Cooling); logout refreshes time-out (Completed + AC Off).
    /// </summary>
    Task<AcReentryResult?> TryInWindowAcReentryAsync(long teacherAccountId, long scheduleId, long classroomId, DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default);
}

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string usernameOrEmail, string password);
    Task LogoutAsync();
    Task<AuthenticatedUser?> GetCurrentUserAsync();
    Task<AuthenticatedUser?> RestoreUserAsync(long accountId, CancellationToken cancellationToken = default);
    Task<bool> IsInitialAdminSetupRequiredAsync();
    Task<OperationResult> CompleteInitialAdminSetupAsync(string email);
    Task<OperationResult> UpdateAdministratorCredentialsAsync(string email, string currentPassword, string? newPassword);
    Task<OperationResult> CreateTeacherAsync(CreateTeacherRequest request);
    Task<OperationResult> UpdateAdminTeacherAsync(UpdateAdminTeacherRequest request);
    Task<IReadOnlyList<TeacherDirectoryEntry>> GetTeachersAsync();
    Task<TeacherProfileEntry?> GetTeacherProfileForAdminAsync(long accountId);
    Task<OperationResult> DeleteTeacherAsync(long accountId);
    Task<IReadOnlyList<ClassroomEntry>> GetClassroomsAsync();
    Task<OperationResult> CreateClassroomAsync(CreateClassroomRequest request);
    Task<OperationResult> UpdateClassroomAsync(UpdateClassroomRequest request);
    Task<OperationResult> DeleteClassroomAsync(long classroomId);
    Task<OperationResult> CreateScheduleAsync(CreateScheduleRequest request);
    Task<IReadOnlyList<ScheduleDirectoryEntry>> GetSchedulesAsync();
    Task<OperationResult> DeleteScheduleAsync(long scheduleId);
    Task<IReadOnlyList<SemesterEntry>> GetSemestersAsync();
    Task<OperationResult> CreateSemesterAsync(CreateSemesterRequest request);
    Task<OperationResult> SetSemesterActiveAsync(long semesterId, bool isActive);
    Task<IReadOnlyList<TeacherSemesterEntry>> GetTeacherSemestersAsync(long? semesterId = null);
    Task<OperationResult> EnrollTeacherInSemesterAsync(long teacherAccountId, long semesterId);
    Task<OperationResult> UnenrollTeacherFromSemesterAsync(long teacherAccountId, long semesterId);
    Task<OperationResult> RequestPasswordResetAsync(string email);
    Task<OperationResult> ResetPasswordAsync(string token, string newPassword, string confirmPassword);
    Task<TeacherProfileEntry?> GetMyTeacherProfileAsync();
    Task<OperationResult> UpdateMyTeacherProfileAsync(UpdateTeacherProfileRequest request);
    Task<IReadOnlyList<ScheduleDirectoryEntry>> GetMyTeacherSchedulesAsync();
    Task<IReadOnlyList<AttendanceLogEntry>> GetAttendanceLogsAsync();
    Task<OperationResult> StartMyClassAsync(long scheduleId);
    Task<OperationResult> EndMyClassAsync();
    Task<IReadOnlyList<TeacherRequestEntry>> GetTeacherRequestsAsync();
    Task<OperationResult> SubmitTeacherRequestAsync(SubmitTeacherRequest request);
    Task<OperationResult> ReviewTeacherRequestAsync(ReviewTeacherRequest request);
    Task<IReadOnlyList<SupportTicketEntry>> GetSupportTicketsAsync();
    Task<OperationResult> SubmitSupportTicketAsync(SubmitSupportTicket request);
    Task<OperationResult> ReviewSupportTicketAsync(ReviewSupportTicket request);
    Task<IReadOnlyList<TemperatureLogEntry>> GetTemperatureLogsAsync(long? classroomId = null);
    Task<OperationResult> SetTemperatureAsync(SetTemperatureRequest request);
    Task<IReadOnlyList<BiometricDeviceEntry>> GetBiometricDevicesAsync();
    Task<OperationResult> CreateBiometricDeviceAsync(CreateBiometricDeviceRequest request);
    Task<OperationResult> ResetSystemToFirstAccessAsync(string currentPassword);
}
