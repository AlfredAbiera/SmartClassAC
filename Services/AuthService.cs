using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace SmartClassAC.Services;

public sealed class AuthService : IAuthService
{
    private readonly IUserAccountRepository _accounts;
    private readonly PasswordHasher _passwordHasher;
    private readonly DefaultAdminOptions _defaultAdmin;
    private readonly PasswordResetOptions _passwordReset;
    private readonly IPasswordResetEmailSender _passwordResetEmailSender;
    private readonly LoginAttemptLimiter _loginAttemptLimiter;
    private readonly SchoolTimeOptions _schoolTime;
    private AuthenticatedUser? _currentUser;

    public AuthService(
        IUserAccountRepository accounts,
        PasswordHasher passwordHasher,
        IOptions<DefaultAdminOptions> defaultAdmin,
        IOptions<PasswordResetOptions> passwordReset,
        IPasswordResetEmailSender passwordResetEmailSender,
        LoginAttemptLimiter loginAttemptLimiter,
        IOptions<SchoolTimeOptions> schoolTime)
    {
        _accounts = accounts;
        _passwordHasher = passwordHasher;
        _defaultAdmin = defaultAdmin.Value;
        _passwordReset = passwordReset.Value;
        _passwordResetEmailSender = passwordResetEmailSender;
        _loginAttemptLimiter = loginAttemptLimiter;
        _schoolTime = schoolTime.Value;
    }

    public async Task<LoginResult> LoginAsync(string usernameOrEmail, string password)
    {
        var identifier = usernameOrEmail.Trim();
        if (_loginAttemptLimiter.IsBlocked(identifier))
        {
            return LoginResult.Failure("Too many unsuccessful attempts. Try again in a few minutes.");
        }

        var account = await _accounts.FindByIdentifierAsync(identifier);
        if (account is null || !_passwordHasher.Verify(password, account.PasswordHash))
        {
            _loginAttemptLimiter.RecordFailure(identifier);
            return LoginResult.Failure("Invalid username or password.");
        }

        _loginAttemptLimiter.RecordSuccess(identifier);

        _currentUser = new AuthenticatedUser(
            account.Id,
            account.Username,
            account.DisplayName,
            account.Email,
            account.Role,
            account.IsDefaultAdmin);

        return LoginResult.Success(_currentUser);
    }

    public Task LogoutAsync()
    {
        _currentUser = null;
        return Task.CompletedTask;
    }

    public async Task<AuthenticatedUser?> RestoreUserAsync(long accountId, CancellationToken cancellationToken = default)
    {
        var account = await _accounts.FindByIdAsync(accountId, cancellationToken);
        if (account is null || account.Id != accountId)
        {
            _currentUser = null;
            return null;
        }

        _currentUser = new AuthenticatedUser(
            account.Id,
            account.Username,
            account.DisplayName,
            account.Email,
            account.Role,
            account.IsDefaultAdmin);
        return _currentUser;
    }

    public Task<AuthenticatedUser?> GetCurrentUserAsync() => Task.FromResult(_currentUser);
    public Task<bool> IsInitialAdminSetupRequiredAsync() => _accounts.IsInitialAdminSetupRequiredAsync();

    public async Task<OperationResult> RequestPasswordResetAsync(string email)
    {
        if (!new EmailAddressAttribute().IsValid(email))
        {
            return OperationResult.Failure("Enter the registered email address for your account.");
        }

        if (!Uri.TryCreate(_passwordReset.PublicBaseUrl, UriKind.Absolute, out var publicBaseUrl) ||
            publicBaseUrl.Scheme is not ("http" or "https"))
        {
            return OperationResult.Failure("Password reset is not configured. Set PasswordReset:PublicBaseUrl in User Secrets.");
        }

        var delivery = await _accounts.CreatePasswordResetAsync(email.Trim(), DateTimeOffset.UtcNow.AddMinutes(15));
        if (delivery is null)
        {
            return OperationResult.Failure("No active account is registered with that email address.");
        }

        var resetUrl = $"{publicBaseUrl.ToString().TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(delivery.RawToken)}";
        try
        {
            await _passwordResetEmailSender.SendAsync(delivery.Email, resetUrl);
            return OperationResult.Success();
        }
        catch (InvalidOperationException exception)
        {
            return OperationResult.Failure(exception.Message);
        }
        catch
        {
            return OperationResult.Failure("The reset email could not be sent. Check the SMTP settings and try again.");
        }
    }

    public async Task<OperationResult> ResetPasswordAsync(string token, string newPassword, string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return OperationResult.Failure("The reset link is invalid or incomplete.");
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return OperationResult.Failure("The new password and confirmation do not match.");
        }

        if (!PasswordPolicy.MeetsRequirements(newPassword))
        {
            return OperationResult.Failure(PasswordPolicy.RequirementMessage);
        }

        return await _accounts.ResetPasswordAsync(token, _passwordHasher.Hash(newPassword))
            ? OperationResult.Success()
            : OperationResult.Failure("This reset link is invalid, expired, or has already been used.");
    }

    public async Task<OperationResult> CompleteInitialAdminSetupAsync(string email)
    {
        if (!new EmailAddressAttribute().IsValid(email))
        {
            return OperationResult.Failure("Enter a valid administrator email address.");
        }

        return await _accounts.SetInitialAdminEmailAsync(email.Trim())
            ? OperationResult.Success()
            : OperationResult.Failure("Initial administrator setup has already been completed.");
    }

    public async Task<OperationResult> UpdateAdministratorCredentialsAsync(string email, string currentPassword, string? newPassword)
    {
        if (_currentUser is not { Role: UserRole.Admin, IsDefaultAdmin: true } currentUser)
        {
            return OperationResult.Failure("Only the default administrator can change these credentials.");
        }

        if (!await IsCurrentPasswordValidAsync(currentUser.Id, currentPassword))
        {
            return OperationResult.Failure("The current administrator password is incorrect.");
        }

        if (!new EmailAddressAttribute().IsValid(email))
        {
            return OperationResult.Failure("Enter a valid administrator email address.");
        }

        if (!string.IsNullOrEmpty(newPassword) && !PasswordPolicy.MeetsRequirements(newPassword))
        {
            return OperationResult.Failure(PasswordPolicy.RequirementMessage);
        }

        var result = await _accounts.UpdateDefaultAdminCredentialsAsync(
            currentUser.Id,
            email.Trim(),
            string.IsNullOrEmpty(newPassword) ? null : _passwordHasher.Hash(newPassword));

        if (result.Succeeded)
        {
            _currentUser = currentUser with { Email = email.Trim() };
        }

        return result;
    }

    public async Task<OperationResult> CreateTeacherAsync(CreateTeacherRequest request)
    {
        var authorization = RequireAdmin();
        if (authorization is not null)
        {
            return authorization;
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Username))
        {
            return OperationResult.Failure("Enter the teacher's name and username.");
        }

        if (string.IsNullOrWhiteSpace(request.EmployeeNumber))
        {
            return OperationResult.Failure("Enter the teacher's employee number.");
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && !new EmailAddressAttribute().IsValid(request.Email.Trim()))
        {
            return OperationResult.Failure("Enter a valid school email address, or leave it blank.");
        }

        if (!MobilePhones.IsValid(request.Phone))
        {
            return OperationResult.Failure(MobilePhones.RequirementMessage);
        }

        if (!RegistrationOtp.Matches(request.RegistrationOtp))
        {
            return OperationResult.Failure(RegistrationOtp.RequirementMessage);
        }

        if (!PasswordPolicy.MeetsRequirements(request.Password))
        {
            return OperationResult.Failure(PasswordPolicy.RequirementMessage);
        }

        var cleanRequest = request with
        {
            DisplayName = request.DisplayName.Trim(),
            Username = request.Username.Trim(),
            EmployeeNumber = request.EmployeeNumber.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Phone = MobilePhones.Normalize(request.Phone),
            DevicePin = string.IsNullOrWhiteSpace(request.DevicePin) ? null : DevicePinCodes.Normalize(request.DevicePin),
            RegistrationOtp = null
        };

        if (cleanRequest.DevicePin is not null && !DevicePinCodes.IsValid(cleanRequest.DevicePin))
        {
            return OperationResult.Failure(DevicePinCodes.RequirementMessage);
        }

        return await _accounts.CreateTeacherAsync(cleanRequest, _passwordHasher.Hash(request.Password));
    }

    public async Task<OperationResult> UpdateAdminTeacherAsync(UpdateAdminTeacherRequest request)
    {
        var authorization = RequireAdmin();
        if (authorization is not null)
        {
            return authorization;
        }

        if (request.AccountId <= 0)
        {
            return OperationResult.Failure("Select a teacher account to update.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Username))
        {
            return OperationResult.Failure("Enter the teacher's name and username.");
        }

        if (string.IsNullOrWhiteSpace(request.EmployeeNumber))
        {
            return OperationResult.Failure("Enter the teacher's employee number.");
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && !new EmailAddressAttribute().IsValid(request.Email.Trim()))
        {
            return OperationResult.Failure("Enter a valid school email address, or leave it blank.");
        }

        if (!MobilePhones.IsValid(request.Phone))
        {
            return OperationResult.Failure(MobilePhones.RequirementMessage);
        }

        string? passwordHash = null;
        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            if (!PasswordPolicy.MeetsRequirements(request.NewPassword))
            {
                return OperationResult.Failure(PasswordPolicy.RequirementMessage);
            }

            passwordHash = _passwordHasher.Hash(request.NewPassword);
        }

        var cleanRequest = request with
        {
            DisplayName = request.DisplayName.Trim(),
            Username = request.Username.Trim(),
            EmployeeNumber = request.EmployeeNumber.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Phone = MobilePhones.Normalize(request.Phone),
            DevicePin = string.IsNullOrWhiteSpace(request.DevicePin) ? null : DevicePinCodes.Normalize(request.DevicePin)
        };

        if (cleanRequest.DevicePin is not null && !DevicePinCodes.IsValid(cleanRequest.DevicePin))
        {
            return OperationResult.Failure(DevicePinCodes.RequirementMessage);
        }

        return await _accounts.UpdateAdminTeacherAsync(cleanRequest, passwordHash);
    }

    public Task<IReadOnlyList<TeacherDirectoryEntry>> GetTeachersAsync() => _accounts.GetTeachersAsync();

    public async Task<TeacherProfileEntry?> GetTeacherProfileForAdminAsync(long accountId)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? null
            : await _accounts.GetTeacherProfileAsync(accountId);
    }

    public async Task<OperationResult> DeleteTeacherAsync(long accountId)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.DeleteTeacherAsync(accountId);
    }

    public async Task<OperationResult> SetTeacherActiveAsync(long accountId, bool isActive)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.SetTeacherActiveAsync(accountId, isActive);
    }

    public Task<IReadOnlyList<ClassroomEntry>> GetClassroomsAsync() => _accounts.GetClassroomsAsync();
    public Task<IReadOnlyList<ScheduleDirectoryEntry>> GetSchedulesAsync() => _accounts.GetSchedulesAsync();

    public async Task<OperationResult> CreateClassroomAsync(CreateClassroomRequest request)
    {
        var authorization = RequireAdmin();
        if (authorization is not null)
        {
            return authorization;
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            return OperationResult.Failure("Enter a classroom name of 100 characters or fewer.");
        }

        if (request.Capacity is < 1 or > 1000)
        {
            return OperationResult.Failure("Classroom capacity must be between 1 and 1,000.");
        }

        if (request.TargetTemperature is < 16 or > 30)
        {
            return OperationResult.Failure("Target temperature must be between 16°C and 30°C.");
        }

        return await _accounts.CreateClassroomAsync(request with { Name = request.Name.Trim() });
    }

    public async Task<OperationResult> UpdateClassroomAsync(UpdateClassroomRequest request)
    {
        var authorization = RequireAdmin();
        if (authorization is not null)
        {
            return authorization;
        }

        if (request.Id <= 0 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            return OperationResult.Failure("Enter a valid classroom name.");
        }

        if (request.Capacity is < 1 or > 1000 || request.TargetTemperature is < 16 or > 30)
        {
            return OperationResult.Failure("Enter a capacity from 1 to 1,000 and a target temperature from 16°C to 30°C.");
        }

        return await _accounts.UpdateClassroomAsync(request with { Name = request.Name.Trim() });
    }

    public async Task<OperationResult> DeleteClassroomAsync(long classroomId)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.DeleteClassroomAsync(classroomId);
    }

    public async Task<OperationResult> CreateScheduleAsync(CreateScheduleRequest request)
    {
        var result = await CreateSchedulesAsync(request with { RecurrencePattern = ScheduleRecurrence.None });
        return result.Succeeded
            ? OperationResult.Success()
            : OperationResult.Failure(result.Error ?? "Schedule could not be saved.");
    }

    public async Task<CreateSchedulesResult> CreateSchedulesAsync(CreateScheduleRequest request)
    {
        var authorization = RequireAdmin();
        if (authorization is not null)
        {
            return CreateSchedulesResult.Failure(authorization.Error ?? "Only an administrator can perform this action.");
        }

        if (request.TeacherAccountId <= 0 || request.ClassroomId <= 0)
        {
            return CreateSchedulesResult.Failure("Select a teacher and a classroom from the database directory.");
        }

        if (request.SemesterId <= 0)
        {
            return CreateSchedulesResult.Failure("Select a semester for this schedule.");
        }

        if (!ScheduleKinds.IsValid(request.ScheduleKind))
        {
            return CreateSchedulesResult.Failure("Schedule kind must be Regular or Makeup.");
        }

        if (string.IsNullOrWhiteSpace(request.SubjectName) || request.SubjectName.Trim().Length > 160)
        {
            return CreateSchedulesResult.Failure("Enter a class subject of 160 characters or fewer.");
        }

        if (request.EndTime <= request.StartTime)
        {
            return CreateSchedulesResult.Failure("The end time must be later than the start time.");
        }

        var pattern = string.IsNullOrWhiteSpace(request.RecurrencePattern)
            ? ScheduleRecurrence.None
            : request.RecurrencePattern.Trim();

        return await _accounts.CreateSchedulesAsync(request with
        {
            SubjectName = request.SubjectName.Trim(),
            ScheduleKind = request.ScheduleKind.Trim(),
            RecurrencePattern = pattern,
            CustomWeekdays = pattern == ScheduleRecurrence.Custom ? request.CustomWeekdays : null
        });
    }

    public async Task<OperationResult> MoveScheduleAsync(MoveScheduleRequest request)
    {
        var authorization = RequireAdmin();
        if (authorization is not null)
        {
            return authorization;
        }

        if (request.ScheduleId <= 0)
        {
            return OperationResult.Failure("Select a schedule to move.");
        }

        if (request.EndTime <= request.StartTime)
        {
            return OperationResult.Failure("The end time must be later than the start time.");
        }

        return await _accounts.MoveScheduleAsync(request);
    }

    public async Task<OperationResult> DeleteScheduleAsync(long scheduleId)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.DeleteScheduleAsync(scheduleId);
    }

    public Task<IReadOnlyList<SemesterEntry>> GetSemestersAsync() => _accounts.GetSemestersAsync();

    public async Task<OperationResult> CreateSemesterAsync(CreateSemesterRequest request)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.CreateSemesterAsync(request);
    }

    public async Task<OperationResult> SetSemesterActiveAsync(long semesterId, bool isActive)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.SetSemesterActiveAsync(semesterId, isActive);
    }

    public Task<IReadOnlyList<TeacherSemesterEntry>> GetTeacherSemestersAsync(long? semesterId = null) =>
        _accounts.GetTeacherSemestersAsync(semesterId);

    public async Task<OperationResult> EnrollTeacherInSemesterAsync(long teacherAccountId, long semesterId)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.EnrollTeacherInSemesterAsync(teacherAccountId, semesterId);
    }

    public async Task<OperationResult> UnenrollTeacherFromSemesterAsync(long teacherAccountId, long semesterId)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.UnenrollTeacherFromSemesterAsync(teacherAccountId, semesterId);
    }

    public async Task<TeacherProfileEntry?> GetMyTeacherProfileAsync()
    {
        if (_currentUser?.Role != UserRole.Teacher)
        {
            return null;
        }

        var profile = await _accounts.GetTeacherProfileAsync(_currentUser.Id);
        return profile is { IsActive: true } ? profile : null;
    }

    public async Task<OperationResult> UpdateMyTeacherProfileAsync(UpdateTeacherProfileRequest request)
    {
        var teacher = RequireTeacher();
        if (teacher is null)
        {
            return OperationResult.Failure("Only a signed-in teacher can update this profile.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 160)
        {
            return OperationResult.Failure("Enter a valid teacher name.");
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && !new EmailAddressAttribute().IsValid(request.Email.Trim()))
        {
            return OperationResult.Failure("Enter a valid email address, or leave it blank.");
        }

        if (!MobilePhones.IsValid(request.Phone))
        {
            return OperationResult.Failure(MobilePhones.RequirementMessage);
        }

        if (!string.IsNullOrEmpty(request.NewPassword) && !PasswordPolicy.MeetsRequirements(request.NewPassword))
        {
            return OperationResult.Failure(PasswordPolicy.RequirementMessage);
        }

        if (string.IsNullOrWhiteSpace(request.DevicePin) || !DevicePinCodes.IsValid(request.DevicePin))
        {
            return OperationResult.Failure(DevicePinCodes.RequirementMessage);
        }

        var result = await _accounts.UpdateTeacherProfileAsync(
            teacher.Id,
            request with
            {
                DisplayName = request.DisplayName.Trim(),
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
                Phone = MobilePhones.Normalize(request.Phone),
                DevicePin = DevicePinCodes.Normalize(request.DevicePin)
            },
            string.IsNullOrEmpty(request.NewPassword) ? null : _passwordHasher.Hash(request.NewPassword));

        if (result.Succeeded)
        {
            _currentUser = teacher with { DisplayName = request.DisplayName.Trim(), Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim() };
        }

        return result;
    }

    public Task<IReadOnlyList<ScheduleDirectoryEntry>> GetMyTeacherSchedulesAsync() =>
        _currentUser?.Role == UserRole.Teacher
            ? _accounts.GetTeacherSchedulesAsync(_currentUser.Id)
            : Task.FromResult<IReadOnlyList<ScheduleDirectoryEntry>>(Array.Empty<ScheduleDirectoryEntry>());

    public async Task<IReadOnlyList<AttendanceLogEntry>> GetAttendanceLogsAsync()
    {
        if (_currentUser is null)
        {
            return Array.Empty<AttendanceLogEntry>();
        }

        await ReconcileAttendanceAsync();
        return _currentUser.Role == UserRole.Teacher
            ? await _accounts.GetAttendanceLogsAsync(_currentUser.Id)
            : _currentUser.Role == UserRole.Admin
                ? await _accounts.GetAttendanceLogsAsync()
                : Array.Empty<AttendanceLogEntry>();
    }

    public async Task<OperationResult> StartMyClassAsync(long scheduleId)
    {
        var teacher = RequireTeacher();
        if (teacher is null)
        {
            return OperationResult.Failure("Only a signed-in teacher can start a class.");
        }

        await ReconcileAttendanceAsync();
        var schoolNow = SchoolClock.GetNow(_schoolTime);
        return await _accounts.StartClassAsync(
            teacher.Id,
            scheduleId,
            DateOnly.FromDateTime(schoolNow),
            TimeOnly.FromDateTime(schoolNow));
    }

    public async Task<OperationResult> EndMyClassAsync()
    {
        var teacher = RequireTeacher();
        if (teacher is null)
        {
            return OperationResult.Failure("Only a signed-in teacher can end a class.");
        }

        await ReconcileAttendanceAsync();
        return await _accounts.EndClassAsync(teacher.Id);
    }

    public Task<IReadOnlyList<TeacherRequestEntry>> GetTeacherRequestsAsync() =>
        _currentUser?.Role == UserRole.Teacher
            ? _accounts.GetTeacherRequestsAsync(_currentUser.Id)
            : _currentUser?.Role == UserRole.Admin
                ? _accounts.GetTeacherRequestsAsync()
                : Task.FromResult<IReadOnlyList<TeacherRequestEntry>>(Array.Empty<TeacherRequestEntry>());

    public async Task<OperationResult> SubmitTeacherRequestAsync(SubmitTeacherRequest request)
    {
        var teacher = RequireTeacher();
        if (teacher is null)
        {
            return OperationResult.Failure("Only a signed-in teacher can submit a request.");
        }

        if (request.RequestType is not ("Leave" or "EarlyOut" or "Overtime"))
        {
            return OperationResult.Failure("Choose a valid request type.");
        }

        var today = SchoolClock.Today(_schoolTime);
        if (request.RequestType == "Leave" && request.RequestDate < today)
        {
            return OperationResult.Failure("Leave requests cannot be submitted for a past date.");
        }

        if (request.RequestType is "EarlyOut" or "Overtime" && request.RequestDate != today)
        {
            return OperationResult.Failure("Early-out and overtime requests must be submitted for today.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 1000)
        {
            return OperationResult.Failure("Enter a request reason of 1,000 characters or fewer.");
        }

        return await _accounts.SubmitTeacherRequestAsync(teacher.Id, request with { Reason = request.Reason.Trim() });
    }

    public async Task<OperationResult> ReviewTeacherRequestAsync(ReviewTeacherRequest request)
    {
        var admin = RequireAdmin();
        if (admin is not null)
        {
            return admin;
        }

        if (request.Status is not ("Approved" or "Rejected"))
        {
            return OperationResult.Failure("Select Approved or Rejected.");
        }

        return await _accounts.ReviewTeacherRequestAsync(_currentUser!.Id, request);
    }

    public Task<IReadOnlyList<SupportTicketEntry>> GetSupportTicketsAsync() =>
        _currentUser?.Role == UserRole.Teacher
            ? _accounts.GetSupportTicketsAsync(_currentUser.Id)
            : _currentUser?.Role == UserRole.Admin
                ? _accounts.GetSupportTicketsAsync()
                : Task.FromResult<IReadOnlyList<SupportTicketEntry>>(Array.Empty<SupportTicketEntry>());

    public async Task<OperationResult> SubmitSupportTicketAsync(SubmitSupportTicket request)
    {
        var teacher = RequireTeacher();
        if (teacher is null)
        {
            return OperationResult.Failure("Only a signed-in teacher can submit a ticket.");
        }

        if (string.IsNullOrWhiteSpace(request.Category) || string.IsNullOrWhiteSpace(request.Details) || request.Details.Trim().Length > 2000)
        {
            return OperationResult.Failure("Enter a category and ticket details of 2,000 characters or fewer.");
        }

        return await _accounts.SubmitSupportTicketAsync(teacher.Id, request with { Category = request.Category.Trim(), Details = request.Details.Trim() });
    }

    public async Task<OperationResult> ReviewSupportTicketAsync(ReviewSupportTicket request)
    {
        var admin = RequireAdmin();
        if (admin is not null)
        {
            return admin;
        }

        if (request.Status is not ("Resolved" or "Dismissed"))
        {
            return OperationResult.Failure("Select Resolved or Dismissed.");
        }

        return await _accounts.ReviewSupportTicketAsync(_currentUser!.Id, request);
    }

    public Task<IReadOnlyList<TemperatureLogEntry>> GetTemperatureLogsAsync(long? classroomId = null) =>
        _currentUser is null
            ? Task.FromResult<IReadOnlyList<TemperatureLogEntry>>(Array.Empty<TemperatureLogEntry>())
            : _accounts.GetTemperatureLogsAsync(classroomId);

    public async Task<OperationResult> SetTemperatureAsync(SetTemperatureRequest request)
    {
        if (_currentUser is null)
        {
            return OperationResult.Failure("Sign in before sending a temperature command.");
        }

        if (request.ClassroomId <= 0 || request.TargetTemperature is < 16 or > 30 ||
            request.EventType is not ("TargetSet" or "EmergencyOverride" or "HardShutdown" or "RemoteOn" or "RemoteOff"))
        {
            return OperationResult.Failure("Enter a valid classroom, temperature, and command type.");
        }

        if (_currentUser.Role == UserRole.Teacher)
        {
            var today = SchoolClock.Today(_schoolTime);
            var allowed = (await _accounts.GetTeacherSchedulesAsync(_currentUser.Id))
                .Any(schedule => schedule.ClassroomId == request.ClassroomId && schedule.ScheduleDate == today);
            if (!allowed)
            {
                return OperationResult.Failure("Teachers can adjust only a classroom assigned to them today.");
            }
        }

        return await _accounts.SetTemperatureAsync(_currentUser.Id, request);
    }

    public Task<IReadOnlyList<BiometricDeviceEntry>> GetBiometricDevicesAsync() => _accounts.GetBiometricDevicesAsync();

    public async Task<OperationResult> CreateBiometricDeviceAsync(CreateBiometricDeviceRequest request)
    {
        var authorization = RequireAdmin();
        return authorization is not null
            ? authorization
            : await _accounts.CreateBiometricDeviceAsync(request);
    }

    public async Task<OperationResult> RequestClearAllFingerprintDevicesAsync()
    {
        var authorization = RequireAdmin();
        if (authorization is not null)
        {
            return authorization;
        }

        return await _accounts.RequestClearAllFingerprintDevicesAsync();
    }

    public Task<int> CountFingerprintTemplatesAsync() => _accounts.CountFingerprintTemplatesAsync();

    public async Task<OperationResult> ResetSystemToFirstAccessAsync(string currentPassword)
    {
        if (_currentUser is not { Role: UserRole.Admin, IsDefaultAdmin: true })
        {
            return OperationResult.Failure("Only the default administrator can reset the system.");
        }

        if (!await IsCurrentPasswordValidAsync(_currentUser.Id, currentPassword))
        {
            return OperationResult.Failure("The current administrator password is incorrect.");
        }

        if (string.IsNullOrWhiteSpace(_defaultAdmin.Username) || string.IsNullOrWhiteSpace(_defaultAdmin.Password))
        {
            return OperationResult.Failure("Configure DefaultAdmin credentials in User Secrets before resetting the system.");
        }

        await _accounts.ResetSystemToFirstAccessAsync(_defaultAdmin, _passwordHasher.Hash(_defaultAdmin.Password));
        _currentUser = null;
        return OperationResult.Success();
    }

    private OperationResult? RequireAdmin() => _currentUser?.Role == UserRole.Admin
        ? null
        : OperationResult.Failure("Only an administrator can perform this action.");

    private AuthenticatedUser? RequireTeacher() => _currentUser?.Role == UserRole.Teacher ? _currentUser : null;

    private async Task ReconcileAttendanceAsync()
    {
        var schoolNow = SchoolClock.GetNow(_schoolTime);
        await _accounts.ReconcileAttendanceOutcomesAsync(
            DateOnly.FromDateTime(schoolNow),
            TimeOnly.FromDateTime(schoolNow));
    }

    private async Task<bool> IsCurrentPasswordValidAsync(long accountId, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var account = await _accounts.FindByIdAsync(accountId);
        return account is not null && _passwordHasher.Verify(password, account.PasswordHash);
    }
}

public sealed class CustomAuthStateProvider : AuthenticationStateProvider
{
    private const string AccountIdStorageKey = "smartclass.account-id";
    private readonly IAuthService _authService;
    private readonly ProtectedSessionStorage _sessionStorage;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CustomAuthStateProvider(IAuthService authService, ProtectedSessionStorage sessionStorage, IHttpContextAccessor httpContextAccessor)
    {
        _authService = authService;
        _sessionStorage = sessionStorage;
        _httpContextAccessor = httpContextAccessor;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var currentUser = await _authService.GetCurrentUserAsync();
        if (currentUser is null)
        {
            var accountIdClaim = _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (long.TryParse(accountIdClaim, out var cookieAccountId))
            {
                currentUser = await _authService.RestoreUserAsync(cookieAccountId);
            }

            try
            {
                if (currentUser is null)
                {
                    var storedAccount = await _sessionStorage.GetAsync<long>(AccountIdStorageKey);
                    if (storedAccount.Success)
                    {
                        currentUser = await _authService.RestoreUserAsync(storedAccount.Value);
                        if (currentUser is null)
                        {
                            await _sessionStorage.DeleteAsync(AccountIdStorageKey);
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Browser storage is unavailable during prerendering.
            }
        }

        if (currentUser is null)
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUser.Id.ToString()),
            new Claim(ClaimTypes.Name, currentUser.DisplayName),
            new Claim(ClaimTypes.Role, currentUser.Role.ToString())
        };
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "SmartClass")));
    }

    public async Task NotifyUserAuthenticationAsync(long accountId)
    {
        await _sessionStorage.SetAsync(AccountIdStorageKey, accountId);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task NotifyUserLogoutAsync()
    {
        await _sessionStorage.DeleteAsync(AccountIdStorageKey);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
    }
}
