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

public sealed record CreateTeacherRequest(string DisplayName, string Username, string Email, string Password);
public sealed record TeacherDirectoryEntry(long Id, string DisplayName, string Username, string Email, int FingerprintTemplateCount);
public sealed record ClassroomEntry(long Id, string Name, int Capacity, int TargetTemperature, decimal? CurrentTemperature, string AcStatus, bool IsActive);
public sealed record CreateClassroomRequest(string Name, int Capacity, int TargetTemperature);
public sealed record UpdateClassroomRequest(long Id, string Name, int Capacity, int TargetTemperature, bool IsActive);

public sealed record CreateScheduleRequest(
    long TeacherAccountId,
    long ClassroomId,
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
    string SubjectName,
    DateOnly ScheduleDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

public sealed record TeacherProfileEntry(long AccountId, string DisplayName, string Username, string Email, string? Phone, int FingerprintTemplateCount);
public sealed record UpdateTeacherProfileRequest(string DisplayName, string Email, string? Phone, string? NewPassword);

public sealed record AttendanceLogEntry(
    long Id,
    long TeacherAccountId,
    string TeacherDisplayName,
    long ClassroomId,
    string ClassroomName,
    DateTimeOffset TimeInUtc,
    DateTimeOffset? TimeOutUtc,
    string VerificationMethod,
    string Status);

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
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ReviewedUtc);

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
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ResolvedUtc);

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
    DateTimeOffset RecordedUtc);

public sealed record SetTemperatureRequest(long ClassroomId, int TargetTemperature, string EventType, string? Notes = null);

public sealed record PasswordResetDelivery(string Email, string RawToken);

public sealed record UserAccountRecord(long Id, string Username, string DisplayName, string? Email, string PasswordHash, UserRole Role, bool IsDefaultAdmin);

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
    Task<IReadOnlyList<TeacherDirectoryEntry>> GetTeachersAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteTeacherAsync(long accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomEntry>> GetClassroomsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> CreateClassroomAsync(CreateClassroomRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateClassroomAsync(UpdateClassroomRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteClassroomAsync(long classroomId, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateScheduleAsync(CreateScheduleRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduleDirectoryEntry>> GetSchedulesAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteScheduleAsync(long scheduleId, CancellationToken cancellationToken = default);
    Task<PasswordResetDelivery?> CreatePasswordResetAsync(string email, DateTimeOffset expiresUtc, CancellationToken cancellationToken = default);
    Task<bool> ResetPasswordAsync(string rawToken, string passwordHash, CancellationToken cancellationToken = default);
    Task<TeacherProfileEntry?> GetTeacherProfileAsync(long accountId, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateTeacherProfileAsync(long accountId, UpdateTeacherProfileRequest request, string? passwordHash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduleDirectoryEntry>> GetTeacherSchedulesAsync(long accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AttendanceLogEntry>> GetAttendanceLogsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> StartClassAsync(long teacherAccountId, long scheduleId, DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default);
    Task<OperationResult> EndClassAsync(long teacherAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeacherRequestEntry>> GetTeacherRequestsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> SubmitTeacherRequestAsync(long teacherAccountId, SubmitTeacherRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> ReviewTeacherRequestAsync(long adminAccountId, ReviewTeacherRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupportTicketEntry>> GetSupportTicketsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> SubmitSupportTicketAsync(long teacherAccountId, SubmitSupportTicket request, CancellationToken cancellationToken = default);
    Task<OperationResult> ReviewSupportTicketAsync(long adminAccountId, ReviewSupportTicket request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TemperatureLogEntry>> GetTemperatureLogsAsync(long? classroomId = null, CancellationToken cancellationToken = default);
    Task<OperationResult> SetTemperatureAsync(long actorAccountId, SetTemperatureRequest request, CancellationToken cancellationToken = default);
    Task ResetSystemToFirstAccessAsync(DefaultAdminOptions defaultAdmin, string passwordHash, CancellationToken cancellationToken = default);
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
    Task<IReadOnlyList<TeacherDirectoryEntry>> GetTeachersAsync();
    Task<OperationResult> DeleteTeacherAsync(long accountId);
    Task<IReadOnlyList<ClassroomEntry>> GetClassroomsAsync();
    Task<OperationResult> CreateClassroomAsync(CreateClassroomRequest request);
    Task<OperationResult> UpdateClassroomAsync(UpdateClassroomRequest request);
    Task<OperationResult> DeleteClassroomAsync(long classroomId);
    Task<OperationResult> CreateScheduleAsync(CreateScheduleRequest request);
    Task<IReadOnlyList<ScheduleDirectoryEntry>> GetSchedulesAsync();
    Task<OperationResult> DeleteScheduleAsync(long scheduleId);
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
    Task<OperationResult> ResetSystemToFirstAccessAsync(string currentPassword);
}
