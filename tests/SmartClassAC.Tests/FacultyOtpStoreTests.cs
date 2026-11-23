using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class FacultyOtpStoreTests
{
    [MariaDbFact]
    public async Task FingerprintDeletionRequiresOneUseClaimAndConfirmedScannerCompletion()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            await Execute(connection, """
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (1, 'Right thumb', 7);
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                SELECT id, '1000000001' FROM fingerprint_templates WHERE template_identifier=7;
                """);
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master),
                ["EnrollmentSecurity:EnableFingerprintDeletion"] = "true"
            }).Build();
            var credentials = new EnrollmentDeviceCredentialStore(configuration);
            Assert.True((await credentials.StoreNewAsync("1000000001", key, master)).Succeeded);
            var service = new FingerprintDeletionService(configuration);
            var repository = new MariaDbUserAccountRepository(configuration, Options.Create(new SchoolTimeOptions()));
            Assert.False((await service.RequestAsync(1, 1, 7, "Right thumb", "1000000001")).Succeeded);
            Assert.False((await service.RequestAsync(2, 1, 7, "Left thumb", "1000000001")).Succeeded);
            Assert.False((await service.RequestAsync(2, 1, 7, "Right thumb", "1000000002")).Succeeded);
            Assert.True((await service.RequestAsync(2, 1, 7, "Right thumb", "1000000001")).Succeeded);
            Assert.False((await service.RequestAsync(2, 1, 7, "Right thumb", "1000000001")).Succeeded);
            var pending = Assert.Single(await repository.GetTeacherFingerprintsAsync(1));
            Assert.True(pending.IsBlocked);
            Assert.Equal("Pending", pending.DeletionState);
            Assert.False((await service.CancelAsync(1, 7, Guid.NewGuid())).Succeeded);
            Assert.False((await repository.SetTeacherActiveAsync(1, false)).Succeeded);
            Assert.Null(await repository.FindTeacherByFingerprintTemplateIdAsync(7, "1000000001"));
            Assert.False((await repository.SetFingerprintSlotBlockedAsync(7, false)).Succeeded);
            Assert.False((await repository.RequestClearAllFingerprintDevicesAsync()).Succeeded);

            var challenges = new EnrollmentChallengeStore(configuration);
            var replayChallenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", "delete_operation")).Challenge);
            var replayNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var replayTag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", "delete_operation",
                replayChallenge, Encoding.ASCII.GetBytes($"{replayNonce}\n"));
            var replayRequest = new FingerprintDeletionMessage("1000000001", null, 0,
                Convert.ToHexString(replayChallenge).ToLowerInvariant(), replayNonce, Convert.ToHexString(replayTag).ToLowerInvariant());
            Assert.Null(await service.ExecuteAsync("operation", replayRequest with { Tag = new string('0', 64) }));
            Assert.NotNull(await service.ExecuteAsync("operation", replayRequest));
            Assert.Null(await service.ExecuteAsync("operation", replayRequest));
            async Task<EnrollmentRecoveryResponse?> Exchange(string action, string? id = null, ushort slot = 0)
            {
                var challenge = Assert.IsType<byte[]>((await challenges.IssueAsync("1000000001", $"delete_{action}")).Challenge);
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var body = action == "operation" ? $"{nonce}\n"
                    : $"{id}\n{slot}\n{nonce}\n{(action == "claim" ? "claim" : "confirmed")}\n";
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", $"delete_{action}",
                    challenge, Encoding.ASCII.GetBytes(body));
                var request = new FingerprintDeletionMessage("1000000001", id, slot,
                    Convert.ToHexString(challenge).ToLowerInvariant(), nonce, Convert.ToHexString(tag).ToLowerInvariant());
                var response = await service.ExecuteAsync(action, request);
                if (response is not null)
                    Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", $"delete_{action}",
                        challenge, Encoding.ASCII.GetBytes(response.Body), Convert.FromHexString(response.Tag)));
                return response;
            }

            var operation = Assert.IsType<EnrollmentRecoveryResponse>(await Exchange("operation"));
            var operationId = operation.Body.Split('\n')[0];
            Assert.True(Guid.TryParseExact(operationId, "D", out _));
            Assert.Null(await Exchange("finish", operationId, 7));
            Assert.Null(await Exchange("claim", operationId, 8));
            var claims = await Task.WhenAll(Exchange("claim", operationId, 7), Exchange("claim", operationId, 7));
            Assert.Single(claims, item => item is not null);
            Assert.False((await service.CancelAsync(1, 7, Guid.Parse(operationId))).Succeeded);
            Assert.Equal("Claimed", Assert.Single(await repository.GetTeacherFingerprintsAsync(1)).DeletionState);
            Assert.NotNull(await Exchange("finish", operationId, 7));
            Assert.NotNull(await Exchange("finish", operationId, 7));
            Assert.Empty(await repository.GetTeacherFingerprintsAsync(1));
            Assert.DoesNotContain(7, await repository.GetBlockedFingerprintSlotsAsync());
            Assert.Null(await Exchange("claim", operationId, 7));
            await Execute(connection, """
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (1, 'Right index', 7);
                """);
            Assert.NotNull(await Exchange("finish", operationId, 7));
            Assert.Equal("Right index", Assert.Single(await repository.GetTeacherFingerprintsAsync(1)).FingerPosition);
        });
    }

    [MariaDbFact]
    public async Task FingerprintDeletionCancellationPreservesTheFingerprint()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            await Execute(connection, """
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (1, 'Right thumb', 7), (1, 'Left thumb', 8);
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                SELECT id, '1000000001' FROM fingerprint_templates WHERE template_identifier=7;
                """);
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master),
                ["EnrollmentSecurity:EnableFingerprintDeletion"] = "true"
            }).Build();
            Assert.True((await new EnrollmentDeviceCredentialStore(config).StoreNewAsync("1000000001", key, master)).Succeeded);
            var service = new FingerprintDeletionService(config);
            config["EnrollmentSecurity:EnableFingerprintDeletion"] = "false";
            Assert.False((await service.RequestAsync(2, 1, 7, "Right thumb", "1000000001")).Succeeded);
            config["EnrollmentSecurity:EnableFingerprintDeletion"] = "true";
            Assert.False((await service.RequestAsync(2, 1, 8, "Left thumb", "1000000001")).Succeeded);
            config["EnrollmentSecurity:MasterKey"] = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(2, 1, 7, "Right thumb", "1000000001"));
            config["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await Assert.ThrowsAsync<CryptographicException>(() => service.RequestAsync(2, 1, 7, "Right thumb", "1000000001"));
            var repository = new MariaDbUserAccountRepository(config, Options.Create(new SchoolTimeOptions()));
            Assert.False(Assert.Single(await repository.GetTeacherFingerprintsAsync(1), f => f.Slot == 7).IsBlocked);
            config["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master);
            Assert.True((await service.RequestAsync(2, 1, 7, "Right thumb", "1000000001")).Succeeded);
            var deletionId = Guid.Parse(Assert.Single(await repository.GetTeacherFingerprintsAsync(1), f => f.Slot == 7).DeletionRequestId!);
            Assert.True((await service.CancelAsync(1, 7, deletionId)).Succeeded);
            var fingerprint = Assert.Single(await repository.GetTeacherFingerprintsAsync(1), f => f.Slot == 7);
            Assert.False(fingerprint.IsBlocked);
            Assert.Null(fingerprint.DeletionState);
            Assert.NotNull(await repository.FindTeacherByFingerprintTemplateIdAsync(7, "1000000001"));
        });
    }

    [MariaDbFact]
    public async Task AdminCanCorrectFingerprintLabelWithoutChangingSensorMapping()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (1, 'Right thumb', 7), (1, 'Left thumb', 8);
                INSERT INTO fingerprint_template_device_bindings (fingerprint_template_id, device_id)
                SELECT id, '1000000001' FROM fingerprint_templates WHERE template_identifier=7;
                """);
            var (auth, _, services) = await CsvAuthFixture(connection, secret);
            await using (services)
            {
                Assert.False((await auth.RenameTeacherFingerprintAsync(1, 7, "Right thumb", "Left thumb")).Succeeded);
                Assert.False((await auth.RenameTeacherFingerprintAsync(1, 7, "Right index", "Right middle")).Succeeded);
                Assert.False((await auth.RenameTeacherFingerprintAsync(1, 7, "Right thumb", "Invalid")).Succeeded);
                await Execute(connection, "INSERT INTO fingerprint_slot_blocks (template_identifier) VALUES (7), (9);");
                Assert.False((await auth.RenameTeacherFingerprintAsync(1, 7, "Right thumb", "Right index")).Succeeded);
                await Execute(connection, """
                    DELETE FROM fingerprint_slot_blocks WHERE template_identifier=7;
                    INSERT INTO fingerprint_enrollment_preparations
                        (id, device_id, teacher_account_id, finger_position, template_identifier)
                    VALUES ('22222222-2222-2222-2222-222222222222', 1, 1, 'Right index', 9);
                    """);
                Assert.False((await auth.RenameTeacherFingerprintAsync(1, 7, "Right thumb", "Right index")).Succeeded);
                Assert.True((await auth.RenameTeacherFingerprintAsync(1, 7, "Right thumb", "Right middle")).Succeeded);
                var fingerprint = Assert.Single(await auth.GetTeacherFingerprintsAsync(1), f => f.Slot == 7);
                Assert.Equal("Right middle", fingerprint.FingerPosition);
                Assert.Equal("1000000001", fingerprint.DeviceId);
                await auth.RestoreUserAsync(1);
                Assert.False((await auth.RenameTeacherFingerprintAsync(1, 7, "Right middle", "Right ring")).Succeeded);
            }
        });
    }

    [MariaDbFact]
    public async Task CsvTeachersRemainUnverifiedUntilAdminConfirmsSms()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);
                """);
            var (auth, serial, services) = await CsvAuthFixture(connection, secret);
            await using (services)
            {
                var result = await auth.ImportCsvAsync("teachers", "\uFEFFname,employee_number,phone\n\"Doe, Jane\",EMP42,09912345678\nBad,EMP43,invalid\nDuplicate,EMP42,09999999999\nShort,EMP44\n");
                Assert.Null(result.Error);
                Assert.Equal(1, result.ImportedRows);
                Assert.Equal(3, result.SkippedRows);
                Assert.Contains(result.Errors, error => error.StartsWith("Row 5:", StringComparison.Ordinal));
                var teacher = Assert.Single(await auth.GetTeachersAsync(), t => t.EmployeeNumber == "EMP42");
                Assert.Equal("Doe, Jane", teacher.DisplayName);
                Assert.False(teacher.PhoneVerified);
                Assert.Null(await store.ReserveAsync("1000000001", teacher.Phone!, secret));

                var profile = (await auth.GetTeacherProfileForAdminAsync(teacher.Id))!;
                var update = new UpdateAdminTeacherRequest(teacher.Id, profile.DisplayName, profile.Username,
                    profile.EmployeeNumber!, profile.Email, profile.Phone!, null, null);
                Assert.False((await auth.UpdateAdminTeacherAsync(update)).Succeeded);
                Assert.True((await auth.RequestMobileOtpAsync(teacher.Id, teacher.Phone!)).Succeeded);
                var wrong = serial.Code == "000000" ? "000001" : "000000";
                Assert.False((await auth.UpdateAdminTeacherAsync(update with { MobileOtp = wrong })).Succeeded);
                Assert.False((await auth.GetTeachersAsync()).Single(t => t.Id == teacher.Id).PhoneVerified);
                Assert.True((await auth.UpdateAdminTeacherAsync(update with { MobileOtp = serial.Code })).Succeeded);
                Assert.True((await auth.GetTeachersAsync()).Single(t => t.Id == teacher.Id).PhoneVerified);
                Assert.NotNull(await store.ReserveAsync("1000000001", teacher.Phone!, secret));
            }
        });
    }

    [MariaDbFact]
    public async Task CsvSchedulesExpandAndEnrollAtomicallyWhileReportingSkippedRows()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30);
                """);
            var (auth, _, services) = await CsvAuthFixture(connection, secret);
            await using (services)
            {
                var date = SchoolClock.Today(new SchoolTimeOptions()).AddDays(1);
                var semesters = await auth.ImportCsvAsync("semesters",
                    $"name,start_date,end_date,active\nTerm,{date:yyyy-MM-dd},{date.AddDays(14):yyyy-MM-dd},true\nTerm,{date:yyyy-MM-dd},{date.AddDays(14):yyyy-MM-dd},true\n");
                Assert.Equal(1, semesters.ImportedRows);
                Assert.Equal(1, semesters.SkippedRows);
                var teachers = await auth.ImportCsvAsync("teachers",
                    "name,employee_number,phone\nTeacher A,EMP-A,09912345678\nTeacher B,EMP-B,09912345679\n");
                Assert.Equal(2, teachers.ImportedRows);
                var semester = (await auth.GetSemestersAsync()).Single(s => s.Name == "Term");
                var teacherA = (await auth.GetTeachersAsync()).Single(t => t.EmployeeNumber == "EMP-A");
                var teacherB = (await auth.GetTeachersAsync()).Single(t => t.EmployeeNumber == "EMP-B");
                await Execute(connection, $"""
                    INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, {semester.Id});
                    INSERT INTO class_schedules (teacher_account_id, original_teacher_account_id, classroom_id,
                        classroom_name, semester_id, schedule_kind, subject_name, schedule_date, start_time, end_time)
                    VALUES (1, 1, 1, 'Room A', {semester.Id}, 'Regular', 'Existing', '{date.AddDays(7):yyyy-MM-dd}', '09:00:00', '10:00:00');
                    """);
                var csv = "employee_number,semester,classroom,subject,date,start_time,end_time,recurrence,weekdays,kind,replaces_schedule_id\n" +
                    $"EMP-A,Term,Room A,Math,{date:yyyy-MM-dd},09:00,10:00,Weekly,,Regular,\n" +
                    $"EMP-B,Term,Room A,Conflict,{date.AddDays(7):yyyy-MM-dd},09:00,10:00,None,,Regular,\n" +
                    $"EMP-B,Term,Missing,Class,{date:yyyy-MM-dd},11:00,12:00,None,,Regular,\n";
                var schedules = await auth.ImportCsvAsync("schedules", csv);
                Assert.Null(schedules.Error);
                Assert.Equal(1, schedules.ImportedRows);
                Assert.Equal(2, schedules.CreatedRecords);
                Assert.Equal(2, schedules.SkippedRows);
                Assert.Equal(1, schedules.SkippedOccurrences);
                var enrolled = await auth.GetTeacherSemestersAsync(semester.Id);
                Assert.Contains(enrolled, e => e.TeacherAccountId == teacherA.Id);
                Assert.DoesNotContain(enrolled, e => e.TeacherAccountId == teacherB.Id);
                var header = "employee_number,semester,classroom,subject,date,start_time,end_time,recurrence,weekdays,kind,replaces_schedule_id\n";
                var single = await auth.ImportCsvAsync("schedules", header +
                    $"EMP-B,Term,Room A,Original,{date:yyyy-MM-dd},11:00,12:00,None,,Regular,\n");
                Assert.Equal(1, single.CreatedRecords);
                var original = Assert.Single(await auth.GetSchedulesAsync(), s => s.SubjectName == "Original");
                var makeup = await auth.ImportCsvAsync("schedules", header +
                    $"EMP-A,Term,Room A,Makeup,{date:yyyy-MM-dd},13:00,14:00,None,,Makeup,{original.Id}\n");
                Assert.Equal(1, makeup.CreatedRecords);
                Assert.Contains(await auth.GetSchedulesAsync(), s => s.SubjectName == "Makeup" && s.ReplacesScheduleId == original.Id);
                var lateMakeup = await auth.ImportCsvAsync("schedules", header +
                    $"EMP-A,Term,Room A,Late,{date.AddDays(7):yyyy-MM-dd},13:00,14:00,None,,Makeup,{original.Id}\n");
                Assert.Equal(1, lateMakeup.SkippedRows);
                Assert.Contains(lateMakeup.Errors, error => error.Contains("next 7 days", StringComparison.Ordinal));
                var monday = date.AddDays(((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7);
                var custom = await auth.ImportCsvAsync("schedules", header +
                    $"EMP-A,Term,Room A,Custom,{monday:yyyy-MM-dd},16:00,17:00,Custom,Mon,Regular,\n");
                Assert.Equal(monday == date ? 3 : 2, custom.CreatedRecords);
                Assert.Equal(0, custom.SkippedRows);
                var badHeader = await auth.ImportCsvAsync("teachers", "name,name,phone\nBad,EMP-C,09912345670");
                Assert.NotNull(badHeader.Error);
                var malformed = await auth.ImportCsvAsync("teachers", "name,employee_number,phone\nGood,EMP-C,09912345670\n\"unterminated,EMP-D,09912345671");
                Assert.NotNull(malformed.Error);
                Assert.DoesNotContain(await auth.GetTeachersAsync(), t => t.EmployeeNumber == "EMP-C");
                await auth.RestoreUserAsync(teacherA.Id);
                Assert.NotNull((await auth.ImportCsvAsync("semesters", $"name,start_date,end_date\nNo,{date:yyyy-MM-dd},{date:yyyy-MM-dd}")).Error);
            }
        });
    }

    [MariaDbFact]
    public async Task UnverifiedNumbersCannotAuthorizeAttendanceSubstitutionsOrExistingMenuSessions()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            var (attendance, _) = await AttendanceOtpFixture(connection, secret);
            const string device = "1000000001", phone = "09123456789";
            var reservation = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            Assert.True(await store.CompleteDeliveryAsync(device, reservation.RequestId, true));
            var session = Assert.IsType<string>(await store.VerifyAndCreateSessionAsync(device, reservation.Code, secret));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
            }).Build();
            var zone = TimeZoneInfo.GetSystemTimeZones().First(z =>
            {
                var hour = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, z).Hour;
                return hour >= 10 && hour < 20;
            });
            var schoolTime = Options.Create(new SchoolTimeOptions { TimeZoneId = zone.Id });
            var menu = new FacultyDeviceScheduleStore(config, schoolTime);
            Assert.NotNull(await menu.GetTodayAsync(device, session, secret));
            await Execute(connection, """
                UPDATE user_accounts SET phone_verified=FALSE WHERE id=1;
                UPDATE user_accounts SET phone='09987654321', phone_verified=FALSE WHERE id=3;
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (3, 100);
                """);
            Assert.Null(await menu.GetTodayAsync(device, session, secret));
            Assert.Null(await store.ReserveAsync(device, phone, secret));
            Assert.False((await attendance.RequestAsync(2, 101, "in")).Succeeded);
            var serial = new FakeSerial();
            var otp = new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance);
            var substitutions = new ScheduleSubstitutionService(config, schoolTime, otp);
            Assert.False((await substitutions.RequestAsync(2, 101, 3)).Succeeded);
            await Execute(connection, "UPDATE user_accounts SET phone_verified=TRUE WHERE id IN (1, 3);");
            Assert.True((await substitutions.RequestAsync(2, 101, 3)).Succeeded);
            Assert.True((await attendance.RequestAsync(2, 101, "in")).Succeeded);
        });
    }

    private static async Task<(IAuthService Auth, FakeSerial Serial, ServiceProvider Services)> CsvAuthFixture(
        MySqlConnection connection, byte[] secret)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
            ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
        }).Build();
        var options = Options.Create(new SchoolTimeOptions());
        var repository = new MariaDbUserAccountRepository(config, options);
        var serial = new FakeSerial();
        var services = new ServiceCollection();
        services.AddSingleton<IUserAccountRepository>(repository);
        services.AddSingleton(new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance));
        services.AddScoped<CsvImportService>();
        services.AddScoped<IAuthService>(provider => new AuthService(repository, new PasswordHasher(),
            Options.Create(new DefaultAdminOptions()), Options.Create(new PasswordResetOptions()), new LoginAttemptLimiter(),
            options, config, provider));
        var provider = services.BuildServiceProvider();
        var auth = provider.GetRequiredService<IAuthService>();
        Assert.NotNull(await auth.RestoreUserAsync(2));
        return (auth, serial, provider);
    }

    [MariaDbFact]
    public async Task AttendanceOtpStartsAndEndsOnlyApprovedClassOnApprovedDevice()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            var (service, serial) = await AttendanceOtpFixture(connection, secret);
            Assert.False((await service.RequestAsync(1, 101, "in")).Succeeded);
            Assert.False((await service.RequestAsync(2, 101, "out")).Succeeded);
            Assert.True((await service.RequestAsync(2, 101, "in")).Succeeded);
            var timeIn = serial.Code!;
            Assert.False(await service.RedeemAsync("1000000002", timeIn, default));
            Assert.False(await service.RedeemAsync("1000000001", timeIn == "000000" ? "000001" : "000000", default));
            var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.RedeemAsync("1000000001", timeIn, default)));
            Assert.Single(attempts, value => value);
            Assert.False(await service.RedeemAsync("1000000001", timeIn, default));
            await using (var state = new MySqlCommand("""
                SELECT a.verification_method, a.outcome, c.ir_attendance_power
                FROM attendance_logs a JOIN classrooms c ON c.id=a.classroom_id WHERE a.class_schedule_id=101;
                """, connection))
            await using (var reader = await state.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Admin-approved SMS OTP", reader.GetString(0));
                Assert.Equal("OnTime", reader.GetString(1));
                Assert.True(reader.GetBoolean(2));
            }
            Assert.False((await service.RequestAsync(2, 101, "in")).Succeeded);
            Assert.True((await service.RequestAsync(2, 101, "out")).Succeeded);
            var timeOut = serial.Code!;
            Assert.True(await service.RedeemAsync("1000000001", timeOut, default));
            Assert.False(await service.RedeemAsync("1000000001", timeOut, default));
            await using var completed = new MySqlCommand("""
                SELECT a.time_out_utc IS NOT NULL, c.ir_attendance_power,
                    (SELECT COUNT(*) FROM attendance_otp_events)
                FROM attendance_logs a JOIN classrooms c ON c.id=a.classroom_id WHERE a.class_schedule_id=101;
                """, connection);
            await using var completedReader = await completed.ExecuteReaderAsync();
            Assert.True(await completedReader.ReadAsync());
            Assert.True(completedReader.GetBoolean(0));
            Assert.False(completedReader.GetBoolean(1));
            Assert.Equal(2L, completedReader.GetInt64(2));
        });
    }

    [MariaDbFact]
    public async Task AttendanceOtpRejectsExpiredInvalidSupersededAndUnverifiedRequests()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            var (service, serial) = await AttendanceOtpFixture(connection, secret);
            await Execute(connection, "UPDATE class_schedules SET original_teacher_account_id=3, substitution_verified=FALSE WHERE id=101;");
            Assert.False((await service.RequestAsync(2, 101, "in")).Succeeded);
            await Execute(connection, "UPDATE class_schedules SET substitution_verified=TRUE WHERE id=101;");
            Assert.True((await service.RequestAsync(2, 101, "in")).Succeeded);
            var code = serial.Code!;
            await Execute(connection, "UPDATE portal_mobile_otp_requests SET expires_utc=DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND);");
            Assert.False(await service.RedeemAsync("1000000001", code, default));
            Assert.True((await service.RequestAsync(2, 101, "in")).Succeeded);
            code = serial.Code!;
            var wrong = code == "000000" ? "000001" : "000000";
            for (var i = 0; i < 5; i++) Assert.False(await service.RedeemAsync("1000000001", wrong, default));
            Assert.False(await service.RedeemAsync("1000000001", code, default));
            Assert.True((await service.RequestAsync(2, 101, "in")).Succeeded);
            code = serial.Code!;
            await Execute(connection, "UPDATE biometric_devices SET paired_at=DATE_ADD(paired_at, INTERVAL 1 SECOND) WHERE id=1;");
            Assert.False(await service.RedeemAsync("1000000001", code, default));
            await Execute(connection, "DELETE FROM portal_mobile_otp_limits;");
            Assert.True((await service.RequestAsync(2, 101, "in")).Succeeded);
            code = serial.Code!;
            Assert.True(await service.RedeemAsync("1000000001", code, default));
        });
    }

    [MariaDbFact]
    public async Task AttendanceOtpFailsClosedOnUncertainSmsAndInvalidatesEarlierSessions()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            var (service, serial) = await AttendanceOtpFixture(connection, secret);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
            }).Build();
            var failingSerial = new FakeSerial { FailAfterSubmission = true };
            var failingOtp = new PortalMobileOtp(config, new GsmSmsTransport(_ => failingSerial), NullLogger<PortalMobileOtp>.Instance);
            var zone = TimeZoneInfo.GetSystemTimeZones().First(z =>
            {
                var hour = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, z).Hour;
                return hour >= 10 && hour < 20;
            });
            var options = Options.Create(new SchoolTimeOptions { TimeZoneId = zone.Id });
            var failing = new AttendanceOtpService(config, options, failingOtp, new MariaDbUserAccountRepository(config, options));
            Assert.False((await failing.RequestAsync(2, 101, "in")).Succeeded);
            Assert.NotNull(failingSerial.Code);
            Assert.False(await service.RedeemAsync("1000000001", failingSerial.Code!, default));

            Assert.True((await service.RequestAsync(2, 101, "in")).Succeeded);
            Assert.True(await service.RedeemAsync("1000000001", serial.Code!, default));
            Assert.True((await service.RequestAsync(2, 101, "out")).Succeeded);
            var previousSessionCode = serial.Code!;
            var repository = new MariaDbUserAccountRepository(config, options);
            Assert.True((await repository.EndClassAsync(1)).Succeeded);
            var now = SchoolClock.GetNow(options.Value);
            Assert.NotNull(await repository.TryInWindowAcReentryAsync(1, 101, 1,
                DateOnly.FromDateTime(now), TimeOnly.FromDateTime(now)));
            Assert.False(await service.RedeemAsync("1000000001", previousSessionCode, default));
            await Execute(connection, "DELETE FROM portal_mobile_otp_limits;");
            Assert.True((await service.RequestAsync(2, 101, "out")).Succeeded);
            Assert.True(await service.RedeemAsync("1000000001", serial.Code!, default));
        });
    }

    [MariaDbFact]
    public async Task AttendanceOtpRouteRequiresDeviceSignatureAndReturnsSignedResult()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            var (service, serial) = await AttendanceOtpFixture(connection, secret);
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                    ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master),
                    ["AttendanceOtp:EnableDeviceApi"] = "true"
                });
                builder.Services.AddSingleton(service);
                await using var app = builder.Build();
                app.MapAttendanceOtpRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                const string device = "1000000001", operation = "attendance_otp_verify";
                Assert.True((await new EnrollmentDeviceCredentialStore(app.Configuration).StoreNewAsync(device, key, master)).Succeeded);
                Assert.True((await service.RequestAsync(2, 101, "in")).Succeeded);
                await app.StartAsync();
                var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                using var challengeResponse = await client.PostAsJsonAsync("api/device/attendance/otp/challenge", new FacultyOtpChallengeRequest(device));
                var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>())!.Challenge;
                var challengeBytes = Convert.FromHexString(challenge);
                var body = Encoding.ASCII.GetBytes(serial.Code + "\n");
                var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, device, operation, challengeBytes, body);
                var request = new FacultyOtpRequest(device, serial.Code, challenge, Convert.ToHexString(tag).ToLowerInvariant());
                using var invalid = await client.PostAsJsonAsync("api/device/attendance/otp/verify", request with { Tag = new string('0', 64) });
                Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
                using var accepted = await client.PostAsJsonAsync("api/device/attendance/otp/verify", request);
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                var response = (await accepted.Content.ReadFromJsonAsync<FacultyOtpResponse>())!;
                Assert.EndsWith("\naccepted\n", response.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, operation,
                    challengeBytes, Encoding.ASCII.GetBytes(response.Body), Convert.FromHexString(response.Tag)));
                using var replay = await client.PostAsJsonAsync("api/device/attendance/otp/verify", request);
                Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
                CryptographicOperations.ZeroMemory(key);
            }
        });
    }

    [MariaDbFact]
    public async Task AttendanceOtpConsumptionRollsBackWhenAcUpdateFails()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            var (service, serial) = await AttendanceOtpFixture(connection, secret);
            Assert.True((await service.RequestAsync(2, 101, "in")).Succeeded);
            var code = serial.Code!;
            await Execute(connection, """
                CREATE TRIGGER reject_attendance_ac BEFORE UPDATE ON classrooms
                FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Simulated AC update failure';
                """);
            await Assert.ThrowsAsync<MySqlException>(() => service.RedeemAsync("1000000001", code, default));
            await Execute(connection, "DROP TRIGGER reject_attendance_ac;");
            await using (var count = new MySqlCommand("SELECT COUNT(*) FROM attendance_logs WHERE class_schedule_id=101;", connection))
                Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
            Assert.True(await service.RedeemAsync("1000000001", code, default));
        });
    }

    private static async Task<(AttendanceOtpService Service, FakeSerial Serial)> AttendanceOtpFixture(
        MySqlConnection connection, byte[] secret)
    {
        var zone = TimeZoneInfo.GetSystemTimeZones().First(z =>
        {
            var hour = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, z).Hour;
            return hour >= 10 && hour < 20;
        });
        var options = new SchoolTimeOptions { TimeZoneId = zone.Id };
        var now = SchoolClock.GetNow(options);
        var start = now.AddMinutes(10);
        var end = now.AddMinutes(70);
        await Execute(connection, $"""
            INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
            VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1), (3, 'original', 'Original', 'hash', 'Teacher', 1);
            INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30), (2, 'Room B', 30);
            UPDATE biometric_devices SET classroom_id=1 WHERE id=1;
            INSERT INTO biometric_devices (id, device_id, display_name, is_active, paired_at, classroom_id)
            VALUES (2, '1000000002', 'Device 2', 1, CURRENT_TIMESTAMP, 2);
            INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);
            INSERT INTO semesters (id, name, start_date, end_date, is_active)
            VALUES (100, 'Test', '{now:yyyy-MM-dd}', '{now.AddDays(1):yyyy-MM-dd}', 1);
            INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 100);
            INSERT INTO class_schedules (id, teacher_account_id, original_teacher_account_id, classroom_id,
                classroom_name, semester_id, schedule_kind, subject_name, schedule_date, start_time, end_time)
            VALUES (101, 1, 1, 1, 'Room A', 100, 'Regular', 'Class', '{now:yyyy-MM-dd}', '{start:HH:mm:ss}', '{end:HH:mm:ss}');
            """);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
            ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
        }).Build();
        var serial = new FakeSerial();
        var otp = new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance);
        var schoolTime = Options.Create(options);
        return (new AttendanceOtpService(config, schoolTime, otp, new MariaDbUserAccountRepository(config, schoolTime)), serial);
    }

    [MariaDbFact]
    public async Task LifecycleEnforcesDeliveryExpiryAttemptsReplayAndAccountStatus()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            const string device = "1000000001";
            const string phone = "09123456789";
            var first = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            Assert.Matches("^[0-9]{6}$", first.Code);
            Assert.False(await store.VerifyAsync(device, first.Code, secret));
            Assert.True(await store.CompleteDeliveryAsync(device, first.RequestId, true));
            Assert.True(await store.VerifyAsync(device, first.Code, secret));
            Assert.False(await store.VerifyAsync(device, first.Code, secret));

            var second = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            Assert.True(await store.CompleteDeliveryAsync(device, second.RequestId, false));
            Assert.False(await store.CompleteDeliveryAsync(device, second.RequestId, true));
            Assert.False(await store.VerifyAsync(device, second.Code, secret));
            var third = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            Assert.True(await store.CompleteDeliveryAsync(device, third.RequestId, true));
            var wrong = third.Code == "000000" ? "000001" : "000000";
            for (var i = 0; i < 5; i++) Assert.False(await store.VerifyAsync(device, wrong, secret));
            Assert.False(await store.VerifyAsync(device, third.Code, secret));
            Assert.Null(await store.ReserveAsync(device, phone, secret));

            await Execute(connection, "UPDATE faculty_otp_phone_limits SET window_start_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 290 SECOND);");
            Assert.Null(await store.ReserveAsync(device, phone, secret));
            await Execute(connection, "UPDATE faculty_otp_phone_limits SET window_start_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 300 SECOND);");
            var expired = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            Assert.True(await store.CompleteDeliveryAsync(device, expired.RequestId, true));
            await Execute(connection, "UPDATE faculty_otp_requests SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND);");
            Assert.False(await store.VerifyAsync(device, expired.Code, secret));
            var inactive = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            Assert.True(await store.CompleteDeliveryAsync(device, inactive.RequestId, true));
            await Execute(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 1;");
            Assert.False(await store.VerifyAsync(device, inactive.Code, secret));
            await Execute(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 1;");
            Assert.False(await store.VerifyAsync(device, inactive.Code, secret));
            var changed = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            Assert.True(await store.CompleteDeliveryAsync(device, changed.RequestId, true));
            await Execute(connection, "UPDATE user_accounts SET phone = '09999999999' WHERE id = 1;");
            Assert.False(await store.VerifyAsync(device, changed.Code, secret));
        });
    }

    [MariaDbFact]
    public async Task LimitsApplyToUnknownPhonesAndDevicesAndReservationsSupersede()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            const string device = "1000000001";
            const string phone = "09123456789";
            var original = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            var replacement = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            Assert.False(await store.CompleteDeliveryAsync(device, original.RequestId, true));
            Assert.True(await store.CompleteDeliveryAsync(device, replacement.RequestId, true));
            Assert.Null(await store.ReserveAsync(device, "09999999999", secret));
            Assert.False(await store.VerifyAsync(device, replacement.Code, secret));
            for (var i = 0; i < 7; i++)
                Assert.Null(await store.ReserveAsync(device, "09" + i.ToString("D9"), secret));
            Assert.Null(await store.ReserveAsync(device, phone, secret));
            await Execute(connection, "UPDATE faculty_otp_device_limits SET window_start_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 290 SECOND);");
            Assert.Null(await store.ReserveAsync(device, phone, secret));
            await Execute(connection, "UPDATE faculty_otp_device_limits SET window_start_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 300 SECOND);");
            Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, phone, secret));
            await Execute(connection, "UPDATE biometric_devices SET is_active = 0 WHERE device_id = '1000000001';");
            Assert.Null(await store.ReserveAsync(device, phone, secret));
            Assert.Null(await store.ReserveAsync("1000000099", phone, secret));
            Assert.Null(await store.ReserveAsync(device, "09١٢٣٤٥٦٧٨٩", secret));
        });
    }

    [MariaDbFact]
    public async Task ConcurrentVerificationCanSucceedOnlyOnce()
    {
        await WithDatabase(async (store, _, secret) =>
        {
            var reservation = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync("1000000001", "09123456789", secret));
            Assert.True(await store.CompleteDeliveryAsync("1000000001", reservation.RequestId, true));
            var results = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => store.VerifyAsync("1000000001", reservation.Code, secret)));
            Assert.Single(results, value => value);
        });
    }

    [MariaDbFact]
    public async Task SessionIsBoundToOtpTeacherScannerAndCurrentAccount()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            const string device = "1000000001";
            await Execute(connection, """
                INSERT INTO biometric_devices (id, device_id, display_name, is_active, paired_at)
                VALUES (2, '1000000002', 'Device 2', 1, CURRENT_TIMESTAMP);
                """);
            async Task<long?> Lookup(string scanner, string token, byte[] key)
            {
                await using var tx = await connection.BeginTransactionAsync();
                var result = await FacultyOtpStore.LockSessionAsync(connection, tx, scanner, token, key);
                await tx.RollbackAsync();
                return result;
            }
            var reservation = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, "09123456789", secret));
            Assert.Null(await store.VerifyAndCreateSessionAsync(device, reservation.Code, secret));
            Assert.True(await store.CompleteDeliveryAsync(device, reservation.RequestId, true));
            var token = Assert.IsType<string>(await store.VerifyAndCreateSessionAsync(device, reservation.Code, secret));
            Assert.Matches("^[0-9a-f]{64}$", token);
            Assert.Null(await store.VerifyAndCreateSessionAsync(device, reservation.Code, secret));
            Assert.Equal(1, await Lookup(device, token, secret));
            Assert.Null(await Lookup("1000000002", token, secret));
            Assert.Null(await Lookup(device, new string('0', 64), secret));
            Assert.Null(await Lookup(device, token, RandomNumberGenerator.GetBytes(32)));
            await Execute(connection, "UPDATE user_accounts SET phone = '09999999999' WHERE id = 1;");
            Assert.Null(await Lookup(device, token, secret));
            await Execute(connection, "UPDATE user_accounts SET phone = '09123456789', is_active = 0 WHERE id = 1;");
            Assert.Null(await Lookup(device, token, secret));
            await Execute(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 1; UPDATE biometric_devices SET paired_at = NULL WHERE id = 1;");
            Assert.Null(await Lookup(device, token, secret));
            await Execute(connection, "UPDATE biometric_devices SET paired_at = DATE_ADD(CURRENT_TIMESTAMP(6), INTERVAL 1 SECOND) WHERE id = 1;");
            Assert.Null(await Lookup(device, token, secret));
            await Execute(connection, "UPDATE faculty_device_sessions SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND);");
            Assert.Null(await Lookup(device, token, secret));

            var next = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, "09123456789", secret));
            Assert.True(await store.CompleteDeliveryAsync(device, next.RequestId, true));
            var results = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => store.VerifyAndCreateSessionAsync(device, next.Code, secret)));
            var newToken = Assert.Single(results, value => value is not null);
            Assert.Equal(1, await Lookup(device, newToken!, secret));
            Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, "09123456789", secret));
            Assert.Null(await Lookup(device, newToken!, secret));
        });
    }

    [MariaDbFact]
    public async Task DeviceScheduleReturnsOnlyCurrentTeachersClassesToday()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30), (2, 'Room B', 30);
                INSERT INTO user_accounts (id, username, display_name, password_hash, role)
                    VALUES (2, 'second', 'Second teacher', 'hash', 'Teacher');
                INSERT INTO class_schedules (teacher_account_id, classroom_id, classroom_name, subject_name, schedule_date, start_time, end_time)
                    VALUES (1, 1, 'Room A', 'Math', '2026-03-01', '08:00:00', '09:00:00'),
                           (1, 2, 'Room B', 'Science', '2026-03-01', '10:00:00', '11:00:00'),
                           (1, 1, 'Room A', 'Tomorrow', '2026-03-02', '08:00:00', '09:00:00'),
                           (2, 1, 'Room A', 'Other teacher', '2026-03-01', '12:00:00', '13:00:00');
                """);
            var reservation = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync("1000000001", "09123456789", secret));
            Assert.True(await store.CompleteDeliveryAsync("1000000001", reservation.RequestId, true));
            var token = Assert.IsType<string>(await store.VerifyAndCreateSessionAsync("1000000001", reservation.Code, secret));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var scheduleStore = new FacultyDeviceScheduleStore(configuration, Options.Create(new SchoolTimeOptions()));
            // Use the configured school date rather than the database server's timezone.
            var today = SchoolClock.Today(new SchoolTimeOptions()).ToString("yyyy-MM-dd");
            await Execute(connection, $"UPDATE class_schedules SET schedule_date = '{today}' WHERE subject_name IN ('Math', 'Science', 'Other teacher');");
            await Execute(connection, $"UPDATE class_schedules SET schedule_date = DATE_ADD('{today}', INTERVAL 1 DAY) WHERE subject_name = 'Tomorrow';");
            var schedules = await scheduleStore.GetTodayAsync("1000000001", token, secret);
            Assert.NotNull(schedules);
            Assert.Equal(new[] { "Math", "Science" }, schedules.Select(value => value.Subject));
            Assert.Equal(new[] { "Room A", "Room B" }, schedules.Select(value => value.Classroom));
            Assert.Null(await scheduleStore.GetTodayAsync("1000000001", new string('0', 64), secret));
            await Execute(connection, "UPDATE faculty_device_sessions SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND);");
            Assert.Null(await scheduleStore.GetTodayAsync("1000000001", token, secret));
        });
    }

    [MariaDbFact]
    public async Task ClassExtensionRejectsTeacherOrRoomConflictsAndCanBeAppliedOnlyOnce()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            const string device = "1000000001";
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30), (2, 'Room B', 30);
                INSERT INTO user_accounts (id, username, display_name, password_hash, role)
                    VALUES (2, 'second', 'Second teacher', 'hash', 'Teacher');
                UPDATE biometric_devices SET classroom_id = 1 WHERE device_id = '1000000001';
                INSERT INTO class_schedules
                    (id, teacher_account_id, classroom_id, classroom_name, subject_name, schedule_date, start_time, end_time)
                VALUES (1, 1, 1, 'Room A', 'Current', '2026-03-01', '09:00:00', '10:00:00'),
                       (2, 1, 2, 'Room B', 'Next', '2026-03-01', '10:20:00', '11:00:00');
                INSERT INTO attendance_logs (class_schedule_id, teacher_account_id, classroom_id, status)
                    VALUES (1, 1, 1, 'Active');
                """);
            var reservation = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, "09123456789", secret));
            Assert.True(await store.CompleteDeliveryAsync(device, reservation.RequestId, true));
            var token = Assert.IsType<string>(await store.VerifyAndCreateSessionAsync(device, reservation.Code, secret));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var extensions = new FacultyDeviceAcStore.FacultyDeviceClassExtensionStore(configuration);
            var date = new DateOnly(2026, 3, 1);
            var time = new TimeOnly(9, 30);

            Assert.Equal(FacultyClassExtensionResult.ScheduleConflict,
                await extensions.ExtendAsync(device, token, secret, date, time));
            await Execute(connection, "UPDATE class_schedules SET teacher_account_id = 2, classroom_id = 1 WHERE id = 2;");
            Assert.Equal(FacultyClassExtensionResult.ScheduleConflict,
                await extensions.ExtendAsync(device, token, secret, date, time));
            await Execute(connection, "UPDATE class_schedules SET start_time = '10:30:00' WHERE id = 2;");
            Assert.Equal(FacultyClassExtensionResult.Extended,
                await extensions.ExtendAsync(device, token, secret, date, time));
            Assert.Equal(FacultyClassExtensionResult.AlreadyExtended,
                await extensions.ExtendAsync(device, token, secret, date, time));
            await using var verify = new MySqlCommand(
                "SELECT class_extension_minutes FROM attendance_logs WHERE class_schedule_id = 1;", connection);
            Assert.Equal(30, Convert.ToInt32(await verify.ExecuteScalarAsync()));
        });
    }

    [MariaDbFact]
    public async Task MenuProtocolAuthenticatesOtpAndBoundScheduleResponses()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                    ["FacultyOtp:Secret"] = Convert.ToBase64String(secret),
                    ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master)
                }).Build();
                const string device = "1000000001";
                Assert.True((await new EnrollmentDeviceCredentialStore(config).StoreNewAsync(device, key, master)).Succeeded);
                var today = SchoolClock.Today(new SchoolTimeOptions()).ToString("yyyy-MM-dd");
                await Execute(connection, $"""
                    INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30);
                    INSERT INTO class_schedules (teacher_account_id, classroom_id, classroom_name, subject_name, schedule_date, start_time, end_time)
                    VALUES (1, 1, 'Room A', 'Mathematics', '{today}', '08:00:00', '09:00:00');
                    """);
                var otp = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, "09123456789", secret));
                Assert.True(await store.CompleteDeliveryAsync(device, otp.RequestId, true));
                var challenges = new EnrollmentChallengeStore(config);
                var protocol = new FacultyDeviceMenuProtocol(config, Options.Create(new SchoolTimeOptions()));
                async Task<(FacultyMenuRequest Request, byte[] Challenge, byte[] Body)> Make(string operation, string value, int index)
                {
                    var challenge = Assert.IsType<byte[]>((await challenges.IssueAsync(device, operation)).Challenge);
                    var body = Encoding.ASCII.GetBytes($"{value}\n{index}\n");
                    var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, device, operation, challenge, body);
                    return (new FacultyMenuRequest(device, value, index, Convert.ToHexString(challenge).ToLowerInvariant(),
                        Convert.ToHexString(tag).ToLowerInvariant()), challenge, body);
                }
                void Check(FacultyOtpResponse response, string purpose, byte[] challenge, byte[] body)
                {
                    Assert.StartsWith(Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant() + "\n", response.Body);
                    Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, purpose, challenge,
                        Encoding.ASCII.GetBytes(response.Body), Convert.FromHexString(response.Tag)));
                }
                var login = await Make("faculty_menu_login", otp.Code, 0);
                Assert.Null(await protocol.ExecuteAsync("faculty_menu_login", login.Request with { Value = "abcdef" }));
                Assert.Null(await protocol.ExecuteAsync("faculty_menu_login", login.Request with { Index = 1 }));
                var response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_login", login.Request));
                Check(response, "faculty_menu_login", login.Challenge, login.Body);
                var lines = response.Body.Split('\n');
                Assert.Equal("accepted", lines[1]);
                var token = lines[2];
                Assert.Matches("^[0-9a-f]{64}$", token);
                Assert.Null(await protocol.ExecuteAsync("faculty_menu_login", login.Request));
                var unassigned = await Make("faculty_menu_ac", token, 0);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_ac", unassigned.Request));
                Check(response, "faculty_menu_ac", unassigned.Challenge, unassigned.Body);
                Assert.EndsWith("\ndenied\n", response.Body);
                await Execute(connection, "UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1; UPDATE classrooms SET target_temperature = 24, minimum_temperature = 22 WHERE id = 1;");
                var ac = await Make("faculty_menu_ac", token, 0);
                Assert.Null(await protocol.ExecuteAsync("faculty_menu_ac", ac.Request with { Index = 1 }));
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_ac", ac.Request));
                Check(response, "faculty_menu_ac", ac.Challenge, ac.Body);
                Assert.EndsWith("\ninfo\n24\n22\n0\n", response.Body);
                Assert.Null(await protocol.ExecuteAsync("faculty_menu_ac", ac.Request));
                await Execute(connection, "INSERT INTO attendance_logs (teacher_account_id, classroom_id) VALUES (1, 1);");
                var activeAc = await Make("faculty_menu_ac", token, 0);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_ac", activeAc.Request));
                Assert.EndsWith("\ninfo\n24\n22\n1\n", response.Body);
                var wrongSession = await Make("faculty_menu_ac", new string('0', 64), 0);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_ac", wrongSession.Request));
                Assert.EndsWith("\ndenied\n", response.Body);
                await Execute(connection, "INSERT INTO classrooms (id, name, capacity, target_temperature, minimum_temperature) VALUES (2, 'Room B', 30, 26, 18); UPDATE biometric_devices SET classroom_id = 2 WHERE id = 1;");
                var reassigned = await Make("faculty_menu_ac", token, 0);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_ac", reassigned.Request));
                Assert.EndsWith("\ninfo\n26\n18\n0\n", response.Body);
                await Execute(connection, "UPDATE classrooms SET is_active = 0 WHERE id = 2;");
                var inactive = await Make("faculty_menu_ac", token, 0);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_ac", inactive.Request));
                Assert.EndsWith("\ndenied\n", response.Body);
                var schedule = await Make("faculty_menu_schedule", token, 0);
                Assert.Null(await protocol.ExecuteAsync("faculty_menu_schedule", schedule.Request with { Index = 1 }));
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_schedule", schedule.Request));
                Check(response, "faculty_menu_schedule", schedule.Challenge, schedule.Body);
                Assert.EndsWith("\nitem\n0\n1\nMathematics\nRoom A\n08:00\n09:00\n", response.Body);
                Assert.Null(await protocol.ExecuteAsync("faculty_menu_schedule", schedule.Request));
                var end = await Make("faculty_menu_schedule", token, 1);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_schedule", end.Request));
                Check(response, "faculty_menu_schedule", end.Challenge, end.Body);
                Assert.EndsWith("\nend\n", response.Body);
                await Execute(connection, "UPDATE user_accounts SET is_active = 0 WHERE id = 1;");
                var denied = await Make("faculty_menu_schedule", token, 0);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_schedule", denied.Request));
                Check(response, "faculty_menu_schedule", denied.Challenge, denied.Body);
                Assert.EndsWith("\ndenied\n", response.Body);
                var disabledAc = await Make("faculty_menu_ac", token, 0);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_menu_ac", disabledAc.Request));
                Assert.EndsWith("\ndenied\n", response.Body);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
                CryptographicOperations.ZeroMemory(key);
            }
        });
    }

    [MariaDbFact]
    public async Task FacultyTargetUpdateRechecksSessionRoomAttendanceAndMinimumAtomically()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity, target_temperature, minimum_temperature)
                VALUES (1, 'Room A', 30, 24, 22), (2, 'Room B', 30, 26, 18);
                UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1;
                """);
            var otp = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync("1000000001", "09123456789", secret));
            Assert.True(await store.CompleteDeliveryAsync("1000000001", otp.RequestId, true));
            var token = Assert.IsType<string>(await store.VerifyAndCreateSessionAsync("1000000001", otp.Code, secret));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var ac = new FacultyDeviceAcStore(config);
            var schoolTime = new SchoolTimeOptions();
            Task<FacultyDeviceTargetResult> Set(string session, int expected, int target) =>
                ac.SetTargetAsync("1000000001", session, secret, 1, expected, target, schoolTime);

            Assert.Equal(FacultyDeviceTargetResult.InvalidTarget, await Set(token, 24, 26));
            Assert.Equal(FacultyDeviceTargetResult.Denied, await Set(new string('0', 64), 24, 25));
            Assert.Equal(FacultyDeviceTargetResult.NoActiveClass, await Set(token, 24, 25));
            await Execute(connection, "INSERT INTO attendance_logs (teacher_account_id, classroom_id) VALUES (1, 1);");
            var competing = await Task.WhenAll(Set(token, 24, 25), Set(token, 24, 25));
            Assert.Single(competing, result => result == FacultyDeviceTargetResult.Updated);
            Assert.Single(competing, result => result == FacultyDeviceTargetResult.StaleTarget);
            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "SELECT COUNT(*) FROM temperature_logs WHERE actor_account_id = 1 AND classroom_id = 1 AND target_temperature = 25 AND event_type = 'TargetSet';";
                Assert.Equal(1L, Convert.ToInt64(await verify.ExecuteScalarAsync()));
            }
            await Execute(connection, "UPDATE classrooms SET minimum_temperature = 25 WHERE id = 1;");
            Assert.Equal(FacultyDeviceTargetResult.BelowMinimum, await Set(token, 25, 24));
            await Execute(connection, "UPDATE biometric_devices SET classroom_id = 2 WHERE id = 1;");
            Assert.Equal(FacultyDeviceTargetResult.Denied, await Set(token, 26, 27));
            Assert.Equal(FacultyDeviceTargetResult.NoActiveClass, await ac.SetTargetAsync("1000000001", token, secret, 2, 26, 27, schoolTime));
            await Execute(connection, "UPDATE attendance_logs SET time_out_utc = UTC_TIMESTAMP(6), status = 'Completed' WHERE teacher_account_id = 1; INSERT INTO attendance_logs (teacher_account_id, classroom_id) VALUES (1, 2); UPDATE classrooms SET is_active = 0 WHERE id = 2;");
            Assert.Equal(FacultyDeviceTargetResult.Denied, await ac.SetTargetAsync("1000000001", token, secret, 2, 26, 27, schoolTime));
            await Execute(connection, "UPDATE classrooms SET is_active = 1 WHERE id = 2; UPDATE faculty_device_sessions SET expires_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND);");
            Assert.Equal(FacultyDeviceTargetResult.Denied, await Set(token, 26, 27));
            await Execute(connection, "UPDATE faculty_device_sessions SET expires_utc = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE); UPDATE user_accounts SET is_active = 0 WHERE id = 1;");
            Assert.Equal(FacultyDeviceTargetResult.Denied, await Set(token, 26, 27));
            await Execute(connection, "UPDATE user_accounts SET is_active = 1 WHERE id = 1; UPDATE biometric_devices SET paired_at = DATE_ADD(paired_at, INTERVAL 1 SECOND) WHERE id = 1;");
            Assert.Equal(FacultyDeviceTargetResult.Denied, await Set(token, 26, 27));
        });
    }

    [MariaDbFact]
    public async Task EarlyTimeInStartsAcAtFifteenMinutesAndSupportsReentry()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            var options = new SchoolTimeOptions();
            var date = SchoolClock.Today(options).AddDays(1);
            await Execute(connection, $"""
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30), (2, 'Room B', 30);
                INSERT INTO semesters (id, name, start_date, end_date, is_active)
                VALUES (100, 'Test', '{date:yyyy-MM-dd}', '{date.AddDays(7):yyyy-MM-dd}', 1);
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 100);
                INSERT INTO class_schedules (id, teacher_account_id, original_teacher_account_id, classroom_id,
                    classroom_name, semester_id, schedule_kind, subject_name, schedule_date, start_time, end_time)
                VALUES (101, 1, 1, 1, 'Room A', 100, 'Regular', 'Class', '{date:yyyy-MM-dd}', '09:00:00', '10:00:00'),
                       (102, 1, 1, 1, 'Room A', 100, 'Makeup', 'Next class', '{date:yyyy-MM-dd}', '10:00:00', '11:00:00');
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var repository = new MariaDbUserAccountRepository(config, Options.Create(options));
            var tooEarly = new TimeOnly(8, 44, 59);
            var opening = new TimeOnly(8, 45);
            Assert.Null(await repository.FindInWindowScheduleIdAsync(1, 1, date, tooEarly));
            Assert.False((await repository.StartClassAsync(1, 101, date, tooEarly)).Succeeded);
            Assert.Null(await repository.FindInWindowScheduleIdAsync(1, 2, date, opening));
            Assert.Equal(101, await repository.FindInWindowScheduleIdAsync(1, 1, date, opening));
            Assert.True((await repository.StartClassAsync(1, 101, date, opening)).Succeeded);
            await using (var state = new MySqlCommand("""
                SELECT a.outcome, c.ac_status, c.ir_attendance_power, s.end_time
                FROM attendance_logs a JOIN classrooms c ON c.id = a.classroom_id
                JOIN class_schedules s ON s.id = a.class_schedule_id WHERE s.id = 101;
                """, connection))
            await using (var reader = await state.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("OnTime", reader.GetString(0));
                Assert.Equal("Cooling", reader.GetString(1));
                Assert.True(reader.GetBoolean(2));
                Assert.Equal(new TimeSpan(10, 0, 0), reader.GetTimeSpan(3));
            }
            Assert.Equal(0, await repository.ReconcileAttendanceOutcomesAsync(date, new TimeOnly(8, 50)));
            await Execute(connection, "UPDATE attendance_logs SET status = 'Completed', time_out_utc = UTC_TIMESTAMP(6) WHERE class_schedule_id = 101;");
            Assert.Null(await repository.TryInWindowAcReentryAsync(1, 101, 1, date, tooEarly));
            Assert.Equal("ac_on", (await repository.TryInWindowAcReentryAsync(1, 101, 1, date, new TimeOnly(8, 50)))?.Action);
            Assert.Equal(101, await repository.FindInWindowScheduleIdAsync(1, 1, date, new TimeOnly(9, 50)));
            Assert.Equal(102, await repository.FindInWindowScheduleIdAsync(1, 1, date, new TimeOnly(10, 0)));
            Assert.Null(await repository.FindInWindowScheduleIdAsync(1, 1, date, new TimeOnly(11, 0)));
        });
    }

    [MariaDbFact]
    public async Task FingerprintScanStartsEarlyAttendanceAndRequestsAc()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            var options = new SchoolTimeOptions();
            var start = SchoolClock.GetNow(options).AddMinutes(10);
            if (start.TimeOfDay >= new TimeSpan(23, 58, 0))
                start = start.Date.AddDays(1).AddMinutes(1);
            var date = DateOnly.FromDateTime(start);
            await Execute(connection, $"""
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30);
                UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1;
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (1, 'Left thumb', 10);
                INSERT INTO semesters (id, name, start_date, end_date, is_active)
                VALUES (100, 'Test', '{date:yyyy-MM-dd}', '{date.AddDays(1):yyyy-MM-dd}', 1);
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 100);
                INSERT INTO class_schedules (id, teacher_account_id, original_teacher_account_id, classroom_id,
                    classroom_name, semester_id, subject_name, schedule_date, start_time, end_time)
                VALUES (101, 1, 1, 1, 'Room A', 100, 'Class', '{date:yyyy-MM-dd}', '{start:HH:mm:ss}', '{start.AddMinutes(1):HH:mm:ss}');
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var repository = new MariaDbUserAccountRepository(config, Options.Create(options));
            var scanner = new DeviceFingerprintService(repository, Options.Create(options), new PasswordHasher(), new LoginAttemptLimiter());
            var result = await scanner.ScanAsync(10, "1000000001");
            Assert.Equal("started", result.Action);
            Assert.Equal(101, result.ScheduleId);
            await using var verify = new MySqlCommand("""
                SELECT a.outcome, c.ac_status, c.ir_attendance_power FROM attendance_logs a
                JOIN classrooms c ON c.id = a.classroom_id WHERE a.class_schedule_id = 101;
                """, connection);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("OnTime", reader.GetString(0));
            Assert.Equal("Cooling", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
        });
    }

    [MariaDbFact]
    public async Task EarlyTimeInCanCrossMidnightAtSemesterStart()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            var options = new SchoolTimeOptions();
            var classDate = SchoolClock.Today(options).AddDays(1);
            var previousDate = classDate.AddDays(-1);
            await Execute(connection, $"""
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30);
                INSERT INTO semesters (id, name, start_date, end_date, is_active)
                VALUES (100, 'Test', '{classDate:yyyy-MM-dd}', '{classDate.AddDays(7):yyyy-MM-dd}', 1);
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 100);
                INSERT INTO class_schedules (id, teacher_account_id, original_teacher_account_id, classroom_id,
                    classroom_name, semester_id, subject_name, schedule_date, start_time, end_time)
                VALUES (101, 1, 1, 1, 'Room A', 100, 'Class', '{classDate:yyyy-MM-dd}', '00:05:00', '01:00:00');
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var repository = new MariaDbUserAccountRepository(config, Options.Create(options));
            Assert.Null(await repository.FindInWindowScheduleIdAsync(1, 1, previousDate, new TimeOnly(23, 49, 59)));
            Assert.Equal(101, await repository.FindInWindowScheduleIdAsync(1, 1, previousDate, new TimeOnly(23, 50)));
            Assert.True((await repository.StartClassAsync(1, 101, previousDate, new TimeOnly(23, 50))).Succeeded);
            Assert.Equal(0, await repository.ReconcileAttendanceOutcomesAsync(previousDate, new TimeOnly(23, 51)));
            await using var verify = new MySqlCommand("""
                SELECT a.outcome, a.status, c.ir_attendance_power FROM attendance_logs a
                JOIN classrooms c ON c.id = a.classroom_id WHERE a.class_schedule_id = 101;
                """, connection);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("OnTime", reader.GetString(0));
            Assert.Equal("Active", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
        });
    }

    [MariaDbFact]
    public async Task AttendancePowerHandlesEarlyEndOverlapReentryAndAutomaticClosure()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            var options = new SchoolTimeOptions();
            var now = SchoolClock.GetNow(options);
            var date = DateOnly.FromDateTime(now);
            var time = TimeOnly.FromDateTime(now);
            await Execute(connection, $"""
                INSERT INTO classrooms (id, name, capacity, target_temperature) VALUES (1, 'Room A', 30, 24);
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'other', 'Other', 'hash', 'Teacher', 1);
                INSERT INTO semesters (id, name, start_date, end_date, is_active)
                VALUES (100, 'Test', '{date.AddDays(-2):yyyy-MM-dd}', '{date.AddDays(2):yyyy-MM-dd}', 1);
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 100), (2, 100);
                INSERT INTO class_schedules (id, teacher_account_id, classroom_id, classroom_name, schedule_date, start_time, end_time, semester_id)
                VALUES (101, 1, 1, 'Room A', '{date:yyyy-MM-dd}', '00:00:00', '23:59:59', 100),
                       (102, 2, 1, 'Room A', '{date:yyyy-MM-dd}', '00:00:00', '23:59:59', 100);
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var repository = new MariaDbUserAccountRepository(config, Options.Create(options));
            async Task<bool> Desired()
            {
                await using var query = new MySqlCommand("SELECT ir_attendance_power FROM classrooms WHERE id = 1;", connection);
                return Convert.ToBoolean(await query.ExecuteScalarAsync());
            }
            Assert.True((await repository.StartClassAsync(1, 101, date, time)).Succeeded);
            Assert.True(await Desired());
            Assert.True((await repository.EndClassAsync(1)).Succeeded);
            Assert.True(await Desired());
            Assert.Equal("ac_on", (await repository.TryInWindowAcReentryAsync(1, 101, 1, date, time))!.Action);
            await Execute(connection, "DELETE FROM class_schedules WHERE id = 102;");
            Assert.True((await repository.EndClassAsync(1)).Succeeded);
            Assert.False(await Desired());

            await Execute(connection, $"""
                UPDATE class_schedules SET schedule_date = '{date.AddDays(-1):yyyy-MM-dd}' WHERE id = 101;
                UPDATE attendance_logs SET time_out_utc = NULL, status = 'Active' WHERE class_schedule_id = 101;
                UPDATE classrooms SET ac_status = 'Cooling', ir_attendance_power = TRUE WHERE id = 1;
                INSERT INTO class_schedules (id, teacher_account_id, classroom_id, classroom_name, schedule_date, start_time, end_time, semester_id)
                VALUES (102, 2, 1, 'Room A', '{date:yyyy-MM-dd}', '00:00:00', '23:59:59', 100);
                """);
            await repository.ReconcileAttendanceOutcomesAsync(date, time);
            Assert.True(await Desired());
            await Execute(connection, "DELETE FROM class_schedules WHERE id = 102;");
            await repository.ReconcileAttendanceOutcomesAsync(date, time);
            Assert.False(await Desired());
        });
    }

    [MariaDbFact]
    public async Task MakeupScheduleReplacesOneOriginalOccurrenceForAllAttendanceAndScheduleQueries()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            var schoolTime = new SchoolTimeOptions { MissingTimeOutGraceMinutes = 0 };
            var date = SchoolClock.Today(schoolTime).AddDays(1);
            await Execute(connection, $"""
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30), (2, 'Room B', 30);
                INSERT INTO semesters (id, name, start_date, end_date, is_active)
                    VALUES (1, 'Makeup test semester', '{date.AddDays(-1):yyyy-MM-dd}', '{date.AddDays(14):yyyy-MM-dd}', 1);
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 1);
                INSERT INTO class_schedules
                    (id, teacher_account_id, classroom_id, classroom_name, semester_id, schedule_kind,
                     subject_name, schedule_date, start_time, end_time)
                    VALUES (1, 1, 1, 'Room A', 1, 'Regular', 'Original class', '{date:yyyy-MM-dd}', '09:00:00', '10:00:00');
                """);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var repository = new MariaDbUserAccountRepository(configuration, Options.Create(schoolTime));

            var outsideWindow = await repository.CreateSchedulesAsync(new CreateScheduleRequest(
                1, 2, 1, ScheduleKinds.Makeup, "Too late", date.AddDays(7), new TimeOnly(10, 0), new TimeOnly(11, 0),
                ReplacesScheduleId: 1));
            Assert.False(outsideWindow.Succeeded);

            var created = await repository.CreateSchedulesAsync(new CreateScheduleRequest(
                1, 2, 1, ScheduleKinds.Makeup, "Replacement class", date, new TimeOnly(10, 0), new TimeOnly(11, 0),
                ReplacesScheduleId: 1));
            Assert.True(created.Succeeded, created.Error);

            var schedules = await repository.GetSchedulesAsync();
            var makeup = Assert.Single(schedules, item => item.ScheduleKind == ScheduleKinds.Makeup);
            Assert.Equal(1, makeup.ReplacesScheduleId);
            Assert.Single(await repository.GetTeacherSchedulesAsync(1));

            Assert.Null(await repository.FindInWindowScheduleIdAsync(1, 1, date, new TimeOnly(9, 30)));
            Assert.Equal(makeup.Id, await repository.FindInWindowScheduleIdAsync(1, 2, date, new TimeOnly(10, 30)));
            Assert.False((await repository.StartClassAsync(1, 1, date, new TimeOnly(9, 30))).Succeeded);

            await repository.ReconcileAttendanceOutcomesAsync(date, new TimeOnly(10, 0));
            await using (var originalAttendance = new MySqlCommand(
                "SELECT COUNT(*) FROM attendance_logs WHERE class_schedule_id = 1;", connection))
                Assert.Equal(0L, Convert.ToInt64(await originalAttendance.ExecuteScalarAsync()));

            Assert.True((await repository.StartClassAsync(1, makeup.Id, date, new TimeOnly(10, 30))).Succeeded);
        });
    }

    [MariaDbFact]
    public async Task SubstitutionRequiresClassBoundSmsAndFingerprintAndPreservesOtherOccurrences()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            var options = new SchoolTimeOptions();
            var date = SchoolClock.Today(options).AddDays(1);
            await Execute(connection, $"""
                INSERT INTO user_accounts (id, username, display_name, phone, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', NULL, 'hash', 'Admin', 1),
                       (3, 'substitute', 'Substitute', '09999999993', 'hash', 'Teacher', 1),
                       (4, 'other', 'Other', '09999999994', 'hash', 'Teacher', 1);
                INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30);
                INSERT INTO semesters (id, name, start_date, end_date, is_active)
                VALUES (100, 'Test', '{date.AddDays(-1):yyyy-MM-dd}', '{date.AddDays(14):yyyy-MM-dd}', 1);
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 100), (3, 100), (4, 100);
                INSERT INTO class_schedules (id, teacher_account_id, original_teacher_account_id, classroom_id,
                    classroom_name, semester_id, subject_name, schedule_date, start_time, end_time)
                VALUES (1, 1, 1, 1, 'Room A', 100, 'Class', '{date:yyyy-MM-dd}', '09:00:00', '10:00:00'),
                       (2, 1, 1, 1, 'Room A', 100, 'Class', '{date.AddDays(7):yyyy-MM-dd}', '09:00:00', '10:00:00');
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
            }).Build();
            var serial = new FakeSerial();
            var otp = new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance);
            var substitutions = new ScheduleSubstitutionService(config, Options.Create(options), otp);
            var repository = new MariaDbUserAccountRepository(config, Options.Create(options));

            Assert.False((await substitutions.RequestAsync(1, 1, 3)).Succeeded);
            Assert.False((await substitutions.ConfirmAsync(2, 1, 3, "123456")).Succeeded);
            Assert.True((await substitutions.RequestAsync(2, 1, 3)).Succeeded);
            var code = Assert.IsType<string>(serial.Code);
            Assert.False((await substitutions.ConfirmAsync(2, 2, 3, code)).Succeeded);
            Assert.False((await substitutions.ConfirmAsync(2, 1, 4, code)).Succeeded);
            Assert.True((await substitutions.ConfirmAsync(2, 1, 3, code)).Succeeded);
            Assert.False((await substitutions.ConfirmAsync(2, 1, 3, code)).Succeeded);

            var schedules = await repository.GetSchedulesAsync();
            Assert.Equal(3, Assert.Single(schedules, s => s.Id == 1).TeacherAccountId);
            Assert.Equal(1, Assert.Single(schedules, s => s.Id == 1).OriginalTeacherAccountId);
            Assert.Equal(1, Assert.Single(schedules, s => s.Id == 2).TeacherAccountId);
            var time = new TimeOnly(9, 30);
            Assert.Null(await repository.FindInWindowScheduleIdAsync(1, 1, date, time));
            Assert.Equal(1, await repository.FindInWindowScheduleIdAsync(3, 1, date, time));
            Assert.False((await repository.StartClassAsync(3, 1, date, time)).Succeeded);
            Assert.True((await repository.StartClassAsync(3, 1, date, time,
                verificationMethod: "Biometric / fingerprint scanner")).Succeeded);
            Assert.False((await substitutions.RemoveAsync(2, 1)).Succeeded);
            await using var ac = new MySqlCommand("SELECT ir_attendance_power FROM classrooms WHERE id = 1;", connection);
            Assert.True(Convert.ToBoolean(await ac.ExecuteScalarAsync()));
            await using var attendance = new MySqlCommand(
                "SELECT COUNT(*) FROM attendance_logs WHERE class_schedule_id = 1 AND teacher_account_id = 1;", connection);
            Assert.Equal(0L, Convert.ToInt64(await attendance.ExecuteScalarAsync()));
        });
    }

    [MariaDbFact]
    public async Task SubstitutionAllowsOnlyVerifiedTeachersFingerprintAtRoomScanner()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            var options = new SchoolTimeOptions();
            var date = SchoolClock.Today(options);
            await Execute(connection, $"""
                INSERT INTO user_accounts (id, username, display_name, phone, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', NULL, 'hash', 'Admin', 1),
                       (3, 'substitute', 'Substitute', '09999999993', 'hash', 'Teacher', 1);
                INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30);
                UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1;
                INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                VALUES (1, 'Left thumb', 10), (3, 'Left thumb', 11);
                INSERT INTO semesters (id, name, start_date, end_date, is_active)
                VALUES (100, 'Test', '{date.AddDays(-1):yyyy-MM-dd}', '{date.AddDays(1):yyyy-MM-dd}', 1);
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 100), (3, 100);
                INSERT INTO class_schedules (id, teacher_account_id, original_teacher_account_id, classroom_id,
                    classroom_name, semester_id, subject_name, schedule_date, start_time, end_time)
                VALUES (1, 1, 1, 1, 'Room A', 100, 'Class', '{date:yyyy-MM-dd}', '00:00:00', '23:59:59');
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
            }).Build();
            var serial = new FakeSerial();
            var otp = new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance);
            var substitutions = new ScheduleSubstitutionService(config, Options.Create(options), otp);
            var repository = new MariaDbUserAccountRepository(config, Options.Create(options));
            var scanner = new DeviceFingerprintService(repository, Options.Create(options), new PasswordHasher(), new LoginAttemptLimiter());
            Assert.Equal("no_schedule", (await scanner.ScanAsync(11, "1000000001")).Action);
            Assert.True((await substitutions.RequestAsync(2, 1, 3)).Succeeded);
            Assert.True((await substitutions.ConfirmAsync(2, 1, 3, Assert.IsType<string>(serial.Code))).Succeeded);
            Assert.Equal("no_schedule", (await scanner.ScanAsync(10, "1000000001")).Action);
            Assert.Equal("started", (await scanner.ScanAsync(11, "1000000001")).Action);
        });
    }

    [MariaDbFact]
    public async Task SubstitutionRejectsConflictsAndInvalidatesCodesAfterRemoval()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            var options = new SchoolTimeOptions();
            var date = SchoolClock.Today(options).AddDays(1);
            await Execute(connection, $"""
                INSERT INTO user_accounts (id, username, display_name, phone, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', NULL, 'hash', 'Admin', 1),
                       (3, 'substitute', 'Substitute', '09999999993', 'hash', 'Teacher', 1),
                       (4, 'admin2', 'Admin 2', NULL, 'hash', 'Admin', 1);
                INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30), (2, 'Room B', 30);
                INSERT INTO semesters (id, name, start_date, end_date, is_active)
                VALUES (100, 'Test', '{date.AddDays(-1):yyyy-MM-dd}', '{date.AddDays(14):yyyy-MM-dd}', 1);
                INSERT INTO teacher_semesters (teacher_account_id, semester_id) VALUES (1, 100), (3, 100);
                INSERT INTO class_schedules (id, teacher_account_id, original_teacher_account_id, classroom_id,
                    classroom_name, semester_id, subject_name, schedule_date, start_time, end_time)
                VALUES (1, 1, 1, 1, 'Room A', 100, 'Class', '{date:yyyy-MM-dd}', '09:00:00', '10:00:00'),
                       (2, 3, 3, 2, 'Room B', 100, 'Conflict', '{date:yyyy-MM-dd}', '09:30:00', '10:30:00');
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
            }).Build();
            var serial = new FakeSerial();
            var otp = new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance);
            var substitutions = new ScheduleSubstitutionService(config, Options.Create(options), otp);
            Assert.False((await substitutions.RequestAsync(2, 1, 3)).Succeeded);
            await Execute(connection, "UPDATE class_schedules SET start_time = '10:00:00', end_time = '11:00:00' WHERE id = 2;");
            Assert.True((await substitutions.RequestAsync(2, 1, 3)).Succeeded);
            var code = Assert.IsType<string>(serial.Code);
            await Execute(connection, "UPDATE class_schedules SET start_time = '09:30:00', end_time = '10:30:00' WHERE id = 2;");
            Assert.False((await substitutions.ConfirmAsync(2, 1, 3, code)).Succeeded);
            await Execute(connection, "DELETE FROM class_schedules WHERE id = 2;");
            Assert.True((await substitutions.RequestAsync(4, 1, 3)).Succeeded);
            var oldCode = Assert.IsType<string>(serial.Code);
            Assert.True((await substitutions.ConfirmAsync(2, 1, 3, code)).Succeeded);
            Assert.True((await substitutions.RemoveAsync(2, 1)).Succeeded);
            Assert.False((await substitutions.ConfirmAsync(4, 1, 3, oldCode)).Succeeded);

            await Execute(connection, "UPDATE class_schedules SET teacher_account_id = 3, substitution_verified = FALSE WHERE id = 1;");
            var repository = new MariaDbUserAccountRepository(config, Options.Create(options));
            Assert.Null(await repository.FindInWindowScheduleIdAsync(3, 1, date, new TimeOnly(9, 30)));
            Assert.False((await repository.StartClassAsync(3, 1, date, new TimeOnly(9, 30),
                verificationMethod: "Biometric / fingerprint scanner")).Succeeded);
            await Execute(connection, """
                INSERT INTO attendance_logs (class_schedule_id, teacher_account_id, classroom_id, status, time_out_utc)
                VALUES (1, 3, 1, 'Completed', UTC_TIMESTAMP(6));
                """);
            Assert.Null(await repository.TryInWindowAcReentryAsync(3, 1, 1, date, new TimeOnly(9, 30)));
            await Execute(connection, "UPDATE attendance_logs SET status = 'Active', time_out_utc = NULL WHERE class_schedule_id = 1;");
            Assert.False((await repository.SetTemperatureAsync(3, new SetTemperatureRequest(1, 24, "TargetSet"))).Succeeded);
            await Execute(connection, "DELETE FROM attendance_logs WHERE class_schedule_id = 1;");
            Assert.True((await substitutions.RequestAsync(2, 1, 3)).Succeeded);
            Assert.True((await substitutions.ConfirmAsync(2, 1, 3, Assert.IsType<string>(serial.Code))).Succeeded);
            Assert.Equal(1, await repository.FindInWindowScheduleIdAsync(3, 1, date, new TimeOnly(9, 30)));
        });
    }

    [MariaDbFact]
    public async Task MissingTimeOutReconciliationWaitsForExtendedClassEnd()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity) VALUES (1, 'Room A', 30);
                INSERT INTO class_schedules
                    (id, teacher_account_id, classroom_id, classroom_name, schedule_date, start_time, end_time)
                VALUES (1, 1, 1, 'Room A', '2026-03-01', '09:00:00', '10:00:00');
                INSERT INTO attendance_logs
                    (class_schedule_id, teacher_account_id, classroom_id, status, class_extension_minutes)
                VALUES (1, 1, 1, 'Active', 30);
                """);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var options = new SchoolTimeOptions { MissingTimeOutGraceMinutes = 0 };
            var repository = new MariaDbUserAccountRepository(configuration, Options.Create(options));
            var date = new DateOnly(2026, 3, 1);
            Assert.Equal(0, await repository.ReconcileAttendanceOutcomesAsync(date, new TimeOnly(10, 29)));
            Assert.Equal(1, await repository.ReconcileAttendanceOutcomesAsync(date, new TimeOnly(10, 30)));
            await using (var verify = new MySqlCommand(
                "SELECT status, outcome FROM attendance_logs WHERE class_schedule_id = 1;", connection))
            await using (var reader = await verify.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Completed", reader.GetString(0));
                Assert.Equal("MissingTimeOut", reader.GetString(1));
            }
            await Execute(connection, "UPDATE user_accounts SET phone_verified=FALSE WHERE id=1;");
            Assert.Null(Assert.Single(await repository.ClaimMissingTimeOutNotificationsAsync()).TeacherPhone);
            await Execute(connection, """
                UPDATE user_accounts SET phone_verified=TRUE WHERE id=1;
                UPDATE attendance_logs SET missing_time_out_sms_status='Pending', missing_time_out_sms_attempted_utc=NULL
                WHERE class_schedule_id=1;
                """);
            Assert.Equal("09123456789", Assert.Single(await repository.ClaimMissingTimeOutNotificationsAsync()).TeacherPhone);
        });
    }

    [MariaDbFact]
    public async Task PowerToggleRequiresVersionedBootAndExplicitReconciliation()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity, target_temperature) VALUES (1, 'Room A', 30, 24);
                UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1;
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["AcIr:EnablePowerToggle"] = "true"
            }).Build();
            var ir = new AcIrStateStore(config);
            const string device = "1000000001";
            var boot = RandomNumberGenerator.GetBytes(32);
            Assert.True(await ir.RegisterBootAsync(device, boot, powerEnabled: true));
            Assert.False(await ir.RegisterBootAsync(device, boot));
            Assert.Null(await ir.ClaimAsync(device, boot, powerEnabled: true));
            Assert.False(await ir.ReconcileAsync(2, device, boot, 1, 24));
            Assert.True(await ir.ReconcileAsync(2, device, boot, 1, 24, assumedPower: false));
            await Execute(connection, "UPDATE classrooms SET target_temperature = 25 WHERE id = 1;");
            Assert.Null(await ir.ClaimAsync(device, boot, powerEnabled: true));
            await Execute(connection, "UPDATE classrooms SET ir_attendance_power = TRUE WHERE id = 1;");
            var claims = await Task.WhenAll(ir.ClaimAsync(device, boot, powerEnabled: true),
                ir.ClaimAsync(device, boot, powerEnabled: true));
            var on = Assert.Single(claims.OfType<AcIrCommand>());
            Assert.True(on.IsPower);
            Assert.Equal(0, on.From);
            Assert.Equal(1, on.To);
            Assert.Null(await ir.ClaimAsync(device, boot));
            Assert.False(await ir.CompleteAsync(device, boot, on.Id));
            Assert.True(await ir.CompleteAsync(device, boot, on.Id, powerEnabled: true));
            Assert.False(await ir.CompleteAsync(device, boot, on.Id, powerEnabled: true));
            Assert.True((await ir.GetAsync(device, boot))!.AssumedPower);
            var temperature = Assert.IsType<AcIrCommand>(await ir.ClaimAsync(device, boot, powerEnabled: true));
            Assert.False(temperature.IsPower);
            Assert.Equal(24, temperature.From);
            Assert.Equal(25, temperature.To);
            Assert.True(await ir.CompleteAsync(device, boot, temperature.Id, powerEnabled: true));
            await Execute(connection, "UPDATE classrooms SET ir_attendance_power = FALSE WHERE id = 1;");
            var off = Assert.IsType<AcIrCommand>(await ir.ClaimAsync(device, boot, powerEnabled: true));
            Assert.True(off.IsPower);
            Assert.Equal(1, off.From);
            Assert.Equal(0, off.To);
            Assert.Null(await ir.ClaimAsync(device, boot, powerEnabled: true));
            var nextBoot = RandomNumberGenerator.GetBytes(32);
            Assert.True(await ir.RegisterBootAsync(device, nextBoot, powerEnabled: true));
            Assert.False(await ir.CompleteAsync(device, boot, off.Id, powerEnabled: true));
            Assert.Null((await ir.GetAsync(device, nextBoot))!.AssumedPower);
            Assert.True(await ir.ReconcileAsync(2, device, nextBoot, 1, 25, assumedPower: false));
            Assert.Null(await ir.ClaimAsync(device, nextBoot, powerEnabled: true));
        });
    }

    [MariaDbFact]
    public async Task RelativeIrStateRequiresReconciliationAndNeverReissuesInflightCommand()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity, target_temperature) VALUES (1, 'Room A', 30, 24), (2, 'Room B', 30, 23);
                UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1;
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var ir = new AcIrStateStore(config);
            const string device = "1000000001";
            var boot = RandomNumberGenerator.GetBytes(32);
            var nextBoot = RandomNumberGenerator.GetBytes(32);
            Assert.True(await ir.RegisterBootAsync(device, boot));
            Assert.True(await ir.RegisterBootAsync(device, boot));
            var adminView = new AcIrAdministration(config);
            Assert.True(Assert.Single(await adminView.GetAsync()).RecentlySeen);
            Assert.Null(await adminView.GetForTeacherAsync(1));
            await Execute(connection, "INSERT INTO attendance_logs (teacher_account_id, classroom_id) VALUES (1, 1);");
            var teacherView = Assert.IsType<AcIrTeacherSnapshot>(await adminView.GetForTeacherAsync(1));
            Assert.Equal(1, teacherView.ClassroomId);
            Assert.Equal("reconcile", teacherView.State);
            Assert.Null(await adminView.GetForTeacherAsync(2));
            await Execute(connection, "UPDATE attendance_logs SET status = 'Completed', time_out_utc = UTC_TIMESTAMP(6) WHERE teacher_account_id = 1;");
            Assert.Null(await adminView.GetForTeacherAsync(1));
            await Execute(connection, "UPDATE ac_ir_state SET last_seen_utc = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 3 MINUTE) WHERE device_id = 1;");
            Assert.False(Assert.Single(await adminView.GetAsync()).RecentlySeen);
            Assert.Null(await ir.ClaimAsync(device, boot));
            Assert.True(Assert.Single(await adminView.GetAsync()).RecentlySeen);
            Assert.Equal(AcIrStatus.Reconcile, (await ir.GetAsync(device, boot))!.Status);
            Assert.Null(await ir.ClaimAsync(device, boot));
            Assert.False(await ir.ReconcileAsync(1, device, boot, 1, 24));
            Assert.False(await ir.ReconcileAsync(2, device, boot, 1, 23));
            Assert.False(await adminView.ReconcileAsync(1, Assert.Single(await adminView.GetAsync())));
            Assert.True(await ir.ReconcileAsync(2, device, boot, 1, 24));
            Assert.Null(await ir.ClaimAsync(device, boot));
            await Execute(connection, "UPDATE classrooms SET target_temperature = 27 WHERE id = 1;");
            var claims = await Task.WhenAll(ir.ClaimAsync(device, boot), ir.ClaimAsync(device, boot));
            var claim = Assert.Single(claims.OfType<AcIrCommand>());
            Assert.Equal(24, claim.From);
            Assert.Equal(25, claim.To);
            Assert.Equal(AcIrStatus.InFlight, (await ir.GetAsync(device, boot))!.Status);
            Assert.False(await ir.ReconcileAsync(2, device, boot, 1, 27));
            Assert.Null(await ir.ClaimAsync(device, boot));
            var repository = new MariaDbUserAccountRepository(config, Options.Create(new SchoolTimeOptions()));
            Assert.False((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 2, 23, 30, true, true, true))).Succeeded);
            Assert.False((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 27, 30, false, true, true))).Succeeded);
            Assert.False((await repository.DeleteBiometricDeviceAsync(1)).Succeeded);
            Assert.False((await repository.UpdateClassroomAsync(new UpdateClassroomRequest(1, "Room A", 27, false, 16))).Succeeded);
            Assert.False((await repository.DeleteClassroomAsync(1)).Succeeded);
            await Execute(connection, "UPDATE biometric_devices SET pairing_code = '123456' WHERE id = 1;");
            Assert.Null(await repository.PairDeviceByCodeAsync("123456"));
            Assert.False(await ir.CompleteAsync(device, boot, RandomNumberGenerator.GetBytes(16)));
            Assert.True(await ir.CompleteAsync(device, boot, claim.Id));
            Assert.False(await ir.CompleteAsync(device, boot, claim.Id));
            Assert.Equal(25, (await ir.GetAsync(device, boot))!.Assumed);
            var interrupted = Assert.IsType<AcIrCommand>(await ir.ClaimAsync(device, boot));
            Assert.True(await ir.RegisterBootAsync(device, nextBoot));
            Assert.False(await ir.CompleteAsync(device, boot, interrupted.Id));
            Assert.False(await ir.RegisterBootAsync(device, boot));
            Assert.Equal(AcIrStatus.Reconcile, (await ir.GetAsync(device, nextBoot))!.Status);
            Assert.True(await ir.ReconcileAsync(2, device, nextBoot, 1, 27));
            await using (var audit = connection.CreateCommand())
            {
                audit.CommandText = "SELECT COUNT(*) FROM ac_ir_reconciliations WHERE device_id = 1 AND classroom_id = 1 AND admin_account_id = 2;";
                Assert.Equal(2L, Convert.ToInt64(await audit.ExecuteScalarAsync()));
            }
            Assert.True((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 2, 23, 30, true, true, true))).Succeeded);
            Assert.Null(await ir.GetAsync(device, nextBoot));
            Assert.False(await ir.RegisterBootAsync(device, nextBoot));
            Assert.Null(await ir.ClaimAsync(device, nextBoot));
            Assert.False(await ir.ReconcileAsync(2, device, nextBoot, 1, 27));
            Assert.True((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 27, 30, true, true, true))).Succeeded);
            Assert.Null(await ir.GetAsync(device, nextBoot));
            Assert.False(await ir.RegisterBootAsync(device, nextBoot));
            var thirdBoot = RandomNumberGenerator.GetBytes(32);
            Assert.True(await ir.RegisterBootAsync(device, thirdBoot));
            Assert.True(await ir.ReconcileAsync(2, device, thirdBoot, 1, 27));
            Assert.NotNull(await repository.PairDeviceByCodeAsync("123456"));
            Assert.Null(await ir.GetAsync(device, thirdBoot));
            Assert.False(await ir.RegisterBootAsync(device, thirdBoot));
            var fourthBoot = RandomNumberGenerator.GetBytes(32);
            Assert.True(await ir.RegisterBootAsync(device, fourthBoot));
            Assert.True(await ir.ReconcileAsync(2, device, fourthBoot, 1, 27));
            Assert.True((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 27, 30, false, true, true))).Succeeded);
            Assert.True((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 27, 30, true, true, true))).Succeeded);
            Assert.Null(await ir.GetAsync(device, fourthBoot));
            Assert.False(await ir.RegisterBootAsync(device, fourthBoot));
            var fifthBoot = RandomNumberGenerator.GetBytes(32);
            Assert.True(await ir.RegisterBootAsync(device, fifthBoot));
            Assert.True(await ir.ReconcileAsync(2, device, fifthBoot, 1, 27));
            Assert.True((await repository.DeleteClassroomAsync(1)).Succeeded);
            Assert.True((await repository.UpdateClassroomAsync(new UpdateClassroomRequest(1, "Room A", 27, true, 16))).Succeeded);
            Assert.Null(await ir.GetAsync(device, fifthBoot));
            Assert.False(await ir.RegisterBootAsync(device, fifthBoot));
        });
    }

    [MariaDbFact]
    public async Task RelativeIrProtocolAuthenticatesRequestsAndDoesNotRepeatClaim()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity, target_temperature) VALUES (1, 'Room A', 30, 24);
                UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1;
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                """);
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                    ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master)
                }).Build();
                const string device = "1000000001";
                Assert.True((await new EnrollmentDeviceCredentialStore(config).StoreNewAsync(device, key, master)).Succeeded);
                var bootBytes = RandomNumberGenerator.GetBytes(32);
                var boot = Convert.ToHexString(bootBytes).ToLowerInvariant();
                var protocol = new AcIrProtocol(config);
                async Task<AcIrRequest> Make(string purpose, string command)
                {
                    var challenge = Assert.IsType<byte[]>((await new EnrollmentChallengeStore(config).IssueAsync(device, purpose)).Challenge);
                    var body = Encoding.ASCII.GetBytes(boot + "\n" + command + "\n");
                    var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, device, purpose, challenge, body);
                    return new AcIrRequest(device, boot, command, Convert.ToHexString(challenge).ToLowerInvariant(),
                        Convert.ToHexString(tag).ToLowerInvariant());
                }
                async Task<FacultyOtpResponse> Run(string purpose, AcIrRequest request)
                {
                    var response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync(purpose, request));
                    var requestBody = Encoding.ASCII.GetBytes(boot + "\n" + request.Command + "\n");
                    Assert.StartsWith(Convert.ToHexString(SHA256.HashData(requestBody)).ToLowerInvariant() + "\n", response.Body);
                    Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, purpose, Convert.FromHexString(request.Challenge!),
                        Encoding.ASCII.GetBytes(response.Body), Convert.FromHexString(response.Tag)));
                    return response;
                }
                var register = await Make("ac_ir_boot", "-");
                Assert.Null(await protocol.ExecuteAsync("ac_ir_boot", register with { Boot = new string('0', 64) }));
                Assert.EndsWith("\nregistered\n", (await Run("ac_ir_boot", register)).Body);
                Assert.Null(await protocol.ExecuteAsync("ac_ir_boot", register));
                var wait = await Make("ac_ir_claim", "-");
                Assert.EndsWith("\nreconcile\n", (await Run("ac_ir_claim", wait)).Body);
                var store = new AcIrStateStore(config);
                Assert.True(await store.ReconcileAsync(2, device, bootBytes, 1, 24));
                await Execute(connection, "UPDATE classrooms SET target_temperature = 25 WHERE id = 1;");
                var claim = await Make("ac_ir_claim", "-");
                var response = await Run("ac_ir_claim", claim);
                var lines = response.Body.Split('\n');
                Assert.Equal("step", lines[1]);
                Assert.Matches("^[0-9a-f]{32}$", lines[2]);
                Assert.Equal("24", lines[3]);
                Assert.Equal("25", lines[4]);
                Assert.Null(await protocol.ExecuteAsync("ac_ir_claim", claim));
                var again = await Make("ac_ir_claim", "-");
                Assert.EndsWith("\ninflight\n", (await Run("ac_ir_claim", again)).Body);
                var complete = await Make("ac_ir_complete", lines[2]);
                Assert.Null(await protocol.ExecuteAsync("ac_ir_complete", complete with { Command = new string('0', 32) }));
                Assert.EndsWith("\ncompleted\n", (await Run("ac_ir_complete", complete)).Body);
                Assert.Null(await protocol.ExecuteAsync("ac_ir_complete", complete));
                var idle = await Make("ac_ir_claim", "-");
                Assert.EndsWith("\nready\n", (await Run("ac_ir_claim", idle)).Body);
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                    ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master),
                    ["AcIr:EnableDeviceApi"] = "true"
                });
                await using var app = builder.Build();
                app.MapAcIrRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                await app.StartAsync();
                var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                using var challengeResponse = await client.PostAsJsonAsync("api/device/ac-ir/claim/challenge", new FacultyOtpChallengeRequest(device));
                Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
                var httpChallenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>())!.Challenge;
                var httpTag = EnrollmentMessageAuthentication.ComputeTag(key, false, device, "ac_ir_claim",
                    Convert.FromHexString(httpChallenge), Encoding.ASCII.GetBytes(boot + "\n-\n"));
                var request = new AcIrRequest(device, boot, "-", httpChallenge, Convert.ToHexString(httpTag).ToLowerInvariant());
                using var httpResponse = await client.PostAsJsonAsync("api/device/ac-ir/claim", request);
                Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
                Assert.True(httpResponse.Headers.CacheControl?.NoStore);
                var httpSigned = (await httpResponse.Content.ReadFromJsonAsync<FacultyOtpResponse>())!;
                Assert.EndsWith("\nready\n", httpSigned.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, "ac_ir_claim",
                    Convert.FromHexString(httpChallenge), Encoding.ASCII.GetBytes(httpSigned.Body), Convert.FromHexString(httpSigned.Tag)));
                using var replay = await client.PostAsJsonAsync("api/device/ac-ir/claim", request);
                Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);

                using var disabledV2 = await client.PostAsJsonAsync("api/device/ac-ir/v2/claim/challenge", new FacultyOtpChallengeRequest(device));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledV2.StatusCode);
                app.Configuration["AcIr:EnablePowerToggle"] = "true";
                using var disabledV1 = await client.PostAsJsonAsync("api/device/ac-ir/claim/challenge", new FacultyOtpChallengeRequest(device));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledV1.StatusCode);
                using var enabledV2 = await client.PostAsJsonAsync("api/device/ac-ir/v2/claim/challenge", new FacultyOtpChallengeRequest(device));
                Assert.Equal(HttpStatusCode.OK, enabledV2.StatusCode);

                bootBytes = RandomNumberGenerator.GetBytes(32);
                boot = Convert.ToHexString(bootBytes).ToLowerInvariant();
                Assert.EndsWith("\nregistered\n", (await Run("ac_ir_v2_boot", await Make("ac_ir_v2_boot", "-"))).Body);
                Assert.True(await store.ReconcileAsync(2, device, bootBytes, 1, 25, assumedPower: false));
                await Execute(connection, "UPDATE classrooms SET ir_attendance_power = TRUE WHERE id = 1;");
                var powerClaim = await Make("ac_ir_v2_claim", "-");
                var powerResponse = await Run("ac_ir_v2_claim", powerClaim);
                var powerLines = powerResponse.Body.Split('\n');
                Assert.Equal("power", powerLines[1]);
                Assert.Equal("0", powerLines[3]);
                Assert.Equal("1", powerLines[4]);
                Assert.Null(await protocol.ExecuteAsync("ac_ir_v2_claim", powerClaim));
                Assert.EndsWith("\ninflight\n", (await Run("ac_ir_v2_claim", await Make("ac_ir_v2_claim", "-"))).Body);
                Assert.EndsWith("\ncompleted\n", (await Run("ac_ir_v2_complete",
                    await Make("ac_ir_v2_complete", powerLines[2]))).Body);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
                CryptographicOperations.ZeroMemory(key);
            }
        });
    }

    [MariaDbFact]
    public async Task RelativeIrClaimAndReassignmentCannotBothSucceed()
    {
        await WithDatabase(async (_, connection, _) =>
        {
            await Execute(connection, """
                INSERT INTO classrooms (id, name, capacity, target_temperature) VALUES (1, 'Room A', 30, 24), (2, 'Room B', 30, 23);
                UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1;
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var ir = new AcIrStateStore(config);
            var repository = new MariaDbUserAccountRepository(config, Options.Create(new SchoolTimeOptions()));
            const string device = "1000000001";
            for (var iteration = 0; iteration < 8; iteration++)
            {
                await Execute(connection, "UPDATE classrooms SET target_temperature = 24 WHERE id = 1;");
                var boot = RandomNumberGenerator.GetBytes(32);
                Assert.True(await ir.RegisterBootAsync(device, boot));
                Assert.True(await ir.ReconcileAsync(2, device, boot, 1, 24));
                await Execute(connection, "UPDATE classrooms SET target_temperature = 25 WHERE id = 1;");
                var claimTask = ir.ClaimAsync(device, boot);
                var assignTask = repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 2, 23, 30, true, true, true));
                await Task.WhenAll(claimTask, assignTask);
                var claim = await claimTask;
                var assigned = (await assignTask).Succeeded;
                Assert.NotEqual(claim is not null, assigned);
                if (claim is not null)
                {
                    Assert.True(await ir.CompleteAsync(device, boot, claim.Id));
                    Assert.True((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 2, 23, 30, true, true, true))).Succeeded);
                }
                Assert.True((await repository.UpdateBiometricDeviceAsync(new UpdateBiometricDeviceRequest(1, 1, 25, 30, true, true, true))).Succeeded);
            }
        });
    }

    [MariaDbFact]
    public async Task MenuRoutesVerifyAndReturnSignedScheduleOverHttp()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                    ["FacultyOtp:EnableDeviceApi"] = "true",
                    ["FacultyOtp:EnableMenuApi"] = "true",
                    ["FacultyOtp:Secret"] = Convert.ToBase64String(secret),
                    ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master)
                });
                builder.Services.Configure<SchoolTimeOptions>(builder.Configuration.GetSection("SchoolTime"));
                await using var app = builder.Build();
                app.MapFacultyDeviceMenuRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                const string device = "1000000001";
                Assert.True((await new EnrollmentDeviceCredentialStore(app.Configuration).StoreNewAsync(device, key, master)).Succeeded);
                var otp = Assert.IsType<FacultyOtpReservation>(await store.ReserveAsync(device, "09123456789", secret));
                Assert.True(await store.CompleteDeliveryAsync(device, otp.RequestId, true));
                await app.StartAsync();
                var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                async Task<FacultyMenuRequest> Make(string route, string purpose, string value)
                {
                    using var response = await client.PostAsJsonAsync($"api/device/faculty/menu/{route}/challenge", new FacultyOtpChallengeRequest(device));
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var challenge = (await response.Content.ReadFromJsonAsync<ChallengeResponse>())!.Challenge;
                    var body = Encoding.ASCII.GetBytes(value + "\n0\n");
                    var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, device, purpose, Convert.FromHexString(challenge), body);
                    return new FacultyMenuRequest(device, value, 0, challenge, Convert.ToHexString(tag).ToLowerInvariant());
                }
                var login = await Make("login", "faculty_menu_login", otp.Code);
                using var invalid = await client.PostAsJsonAsync("api/device/faculty/menu/login", login with { Tag = new string('0', 64) });
                Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
                using var loginResponse = await client.PostAsJsonAsync("api/device/faculty/menu/login", login);
                Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
                Assert.True(loginResponse.Headers.CacheControl?.NoStore);
                var signed = (await loginResponse.Content.ReadFromJsonAsync<FacultyOtpResponse>())!;
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, "faculty_menu_login",
                    Convert.FromHexString(login.Challenge!), Encoding.ASCII.GetBytes(signed.Body), Convert.FromHexString(signed.Tag)));
                var token = signed.Body.Split('\n')[2];
                await Execute(connection, "INSERT INTO classrooms (id, name, capacity, target_temperature, minimum_temperature) VALUES (1, 'Room A', 30, 25, 20); UPDATE biometric_devices SET classroom_id = 1 WHERE id = 1;");
                using var disabledTarget = await client.PostAsJsonAsync("api/device/faculty/menu/ac-target/challenge", new FacultyOtpChallengeRequest(device));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledTarget.StatusCode);
                app.Configuration["FacultyOtp:EnableAcTargetApi"] = "true";
                var ac = await Make("ac", "faculty_menu_ac", token);
                using var acResponse = await client.PostAsJsonAsync("api/device/faculty/menu/ac", ac);
                Assert.Equal(HttpStatusCode.OK, acResponse.StatusCode);
                Assert.True(acResponse.Headers.CacheControl?.NoStore);
                signed = (await acResponse.Content.ReadFromJsonAsync<FacultyOtpResponse>())!;
                Assert.EndsWith("\ninfo\n25\n20\n0\n", signed.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, "faculty_menu_ac",
                    Convert.FromHexString(ac.Challenge!), Encoding.ASCII.GetBytes(signed.Body), Convert.FromHexString(signed.Tag)));
                using var acReplay = await client.PostAsJsonAsync("api/device/faculty/menu/ac", ac);
                Assert.Equal(HttpStatusCode.Forbidden, acReplay.StatusCode);
                var acV2 = await Make("ac-v2", "faculty_menu_ac_v2", token);
                using var acV2Response = await client.PostAsJsonAsync("api/device/faculty/menu/ac-v2", acV2);
                Assert.Equal(HttpStatusCode.OK, acV2Response.StatusCode);
                signed = (await acV2Response.Content.ReadFromJsonAsync<FacultyOtpResponse>())!;
                Assert.EndsWith("\ninfo\n1\n25\n20\n0\n", signed.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, "faculty_menu_ac_v2",
                    Convert.FromHexString(acV2.Challenge!), Encoding.ASCII.GetBytes(signed.Body), Convert.FromHexString(signed.Tag)));
                await Execute(connection, "INSERT INTO attendance_logs (teacher_account_id, classroom_id) VALUES (1, 1);");
                using var targetChallengeResponse = await client.PostAsJsonAsync("api/device/faculty/menu/ac-target/challenge", new FacultyOtpChallengeRequest(device));
                Assert.Equal(HttpStatusCode.OK, targetChallengeResponse.StatusCode);
                var targetChallenge = (await targetChallengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>())!.Challenge;
                var targetBody = Encoding.ASCII.GetBytes(token + "\n2526\n1\n");
                var targetTag = EnrollmentMessageAuthentication.ComputeTag(key, false, device, "faculty_menu_ac_target",
                    Convert.FromHexString(targetChallenge), targetBody);
                var change = new FacultyMenuRequest(device, token, 2526, targetChallenge, Convert.ToHexString(targetTag).ToLowerInvariant(), 1);
                using var tamperedTarget = await client.PostAsJsonAsync("api/device/faculty/menu/ac-target", change with { RoomId = 2 });
                Assert.Equal(HttpStatusCode.Forbidden, tamperedTarget.StatusCode);
                using var savedTarget = await client.PostAsJsonAsync("api/device/faculty/menu/ac-target", change);
                Assert.Equal(HttpStatusCode.OK, savedTarget.StatusCode);
                signed = (await savedTarget.Content.ReadFromJsonAsync<FacultyOtpResponse>())!;
                Assert.EndsWith("\nsaved\n", signed.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, "faculty_menu_ac_target",
                    Convert.FromHexString(targetChallenge), Encoding.ASCII.GetBytes(signed.Body), Convert.FromHexString(signed.Tag)));
                using var targetReplay = await client.PostAsJsonAsync("api/device/faculty/menu/ac-target", change);
                Assert.Equal(HttpStatusCode.Forbidden, targetReplay.StatusCode);
                var schedule = await Make("schedule", "faculty_menu_schedule", token);
                using var scheduleResponse = await client.PostAsJsonAsync("api/device/faculty/menu/schedule", schedule);
                Assert.Equal(HttpStatusCode.OK, scheduleResponse.StatusCode);
                signed = (await scheduleResponse.Content.ReadFromJsonAsync<FacultyOtpResponse>())!;
                Assert.EndsWith("\nend\n", signed.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, "faculty_menu_schedule",
                    Convert.FromHexString(schedule.Challenge!), Encoding.ASCII.GetBytes(signed.Body), Convert.FromHexString(signed.Tag)));
                using var replay = await client.PostAsJsonAsync("api/device/faculty/menu/schedule", schedule);
                Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
                CryptographicOperations.ZeroMemory(key);
            }
        });
    }

    [MariaDbFact]
    public async Task PhoneLimitIsSharedAcrossScanners()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO biometric_devices (id, device_id, display_name, is_active, paired_at)
                VALUES (2, '1000000002', 'Device 2', 1, CURRENT_TIMESTAMP);
                """);
            var requests = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
                store.ReserveAsync(i % 2 == 0 ? "1000000001" : "1000000002", "09123456789", secret)));
            Assert.Equal(3, requests.Count(value => value is not null));
        });
    }

    [MariaDbFact]
    public async Task PortalMobileOtpExpiresLimitsAttemptsAndRejectsCrossPurpose()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
            }).Build();
            var serial = new FakeSerial();
            var otp = new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance);
            var repository = new MariaDbUserAccountRepository(config, Options.Create(new SchoolTimeOptions()));
            const string phone = "09888888888";
            Assert.False((await otp.IssueAsync(1, "admin", 1, phone)).Succeeded);
            Assert.True((await otp.IssueAsync(2, "create", 0, phone)).Succeeded);
            var update = new UpdateAdminTeacherRequest(1, "Teacher", "teacher", "EMP1", null, phone, null, null, serial.Code, 2);
            Assert.False((await repository.UpdateAdminTeacherAsync(update, null)).Succeeded);
            var create = new CreateTeacherRequest("New", "new", "EMP2", null, phone, "password", "4321", serial.Code, 2);
            await Execute(connection, "UPDATE portal_mobile_otp_requests SET expires_utc=DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 SECOND);");
            Assert.False((await repository.CreateTeacherAsync(create, "hash")).Succeeded);
            Assert.True((await otp.IssueAsync(2, "create", 0, phone)).Succeeded);
            create = create with { RegistrationOtp = serial.Code };
            var wrong = serial.Code == "000000" ? "000001" : "000000";
            for (var i = 0; i < 5; i++)
                Assert.False((await repository.CreateTeacherAsync(create with { RegistrationOtp = wrong }, "hash")).Succeeded);
            Assert.False((await repository.CreateTeacherAsync(create, "hash")).Succeeded);
            Assert.True((await otp.IssueAsync(2, "create", 0, phone)).Succeeded);
            Assert.False((await otp.IssueAsync(2, "create", 0, phone)).Succeeded);
            Assert.False((await repository.CreateTeacherAsync(create with { RegistrationOtp = serial.Code }, "hash")).Succeeded);
        });
    }

    [MariaDbFact]
    public async Task RegistrationUsesSmsCodeSeparatelyFromLoginPassword()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1);
                INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
            }).Build();
            var serial = new FakeSerial();
            var otp = new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance);
            var repository = new MariaDbUserAccountRepository(config, Options.Create(new SchoolTimeOptions()));
            var hasher = new PasswordHasher();
            var auth = new AuthService(repository, hasher, Options.Create(new DefaultAdminOptions()),
                Options.Create(new PasswordResetOptions()), new LoginAttemptLimiter(),
                Options.Create(new SchoolTimeOptions()));
            Assert.NotNull(await auth.RestoreUserAsync(2));
            const string phone = "09999999999";
            Assert.True((await otp.IssueAsync(2, "create", 0, phone)).Succeeded);
            var request = new CreateTeacherRequest("New", "new", "EMP2", null, phone,
                "ValidPassword1", "4321", serial.Code);

            var invalidPassword = await auth.CreateTeacherAsync(request with { Password = "123456" });
            Assert.Equal(PasswordPolicy.RequirementMessage, invalidPassword.Error);
            var invalidCode = await auth.CreateTeacherAsync(request with { RegistrationOtp = serial.Code == "000000" ? "000001" : "000000" });
            Assert.Equal("Invalid, expired, or unavailable verification code. Request a new code if needed.", invalidCode.Error);
            Assert.True((await auth.CreateTeacherAsync(request)).Succeeded);
            var account = await repository.FindByIdentifierAsync("new");
            Assert.NotNull(account);
            Assert.True(hasher.Verify("ValidPassword1", account.PasswordHash));
            Assert.False(hasher.Verify(serial.Code!, account.PasswordHash));
        });
    }

    [MariaDbFact]
    public async Task PortalMobileOtpProtectsRegistrationAndNumberChanges()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            await Execute(connection, """
                INSERT INTO user_accounts (id, username, display_name, password_hash, role, is_active)
                VALUES (2, 'admin', 'Admin', 'hash', 'Admin', 1), (3, 'admin2', 'Admin 2', 'hash', 'Admin', 1);
                INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);
                """);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                ["FacultyOtp:Secret"] = Convert.ToBase64String(secret)
            }).Build();
            var serial = new FakeSerial();
            var otp = new PortalMobileOtp(config, new GsmSmsTransport(_ => serial), NullLogger<PortalMobileOtp>.Instance);
            var repository = new MariaDbUserAccountRepository(config, Options.Create(new SchoolTimeOptions()));
            const string phone = "09999999999";
            Assert.True((await otp.IssueAsync(2, "create", 0, phone)).Succeeded);
            var create = new CreateTeacherRequest("New", "new", "EMP2", null, phone, "password",
                "4321", serial.Code, 2);
            Assert.False((await repository.CreateTeacherAsync(create with { VerificationActorId = 3 }, "hash")).Succeeded);
            Assert.False((await repository.CreateTeacherAsync(create with { Phone = "09999999998" }, "hash")).Succeeded);
            Assert.False((await repository.CreateTeacherAsync(create with { Username = "teacher" }, "hash")).Succeeded);
            Assert.True((await repository.CreateTeacherAsync(create, "hash")).Succeeded);
            Assert.False((await repository.CreateTeacherAsync(create with { Username = "new2", EmployeeNumber = "EMP3" }, "hash")).Succeeded);
            Assert.True((await otp.IssueAsync(2, "admin", 1, "09999999997")).Succeeded);
            var update = new UpdateAdminTeacherRequest(1, "Teacher", "teacher", "EMP1", null,
                "09999999997", null, null, serial.Code, 2);
            Assert.False((await repository.UpdateAdminTeacherAsync(update with { MobileOtp = "000000" == serial.Code ? "000001" : "000000" }, null)).Succeeded);
            Assert.True((await repository.UpdateAdminTeacherAsync(update, null)).Succeeded);
            Assert.True((await otp.IssueAsync(1, "self", 1, "09999999996")).Succeeded);
            var profile = new UpdateTeacherProfileRequest("Teacher", null, "09999999996", "1234", null, serial.Code);
            Assert.True((await repository.UpdateTeacherProfileAsync(1, profile, null)).Succeeded);
            Assert.True((await repository.UpdateTeacherProfileAsync(1, profile with { MobileOtp = null }, null)).Succeeded);
            Assert.False((await repository.UpdateTeacherProfileAsync(1, profile with { Phone = "09999999995" }, null)).Succeeded);
            var failed = new PortalMobileOtp(config, new GsmSmsTransport(_ => new FakeSerial { FailAfterSubmission = true }),
                NullLogger<PortalMobileOtp>.Instance);
            Assert.False((await failed.IssueAsync(2, "create", 0, "09999999994")).Succeeded);
            await using var count = new MySqlCommand("SELECT COUNT(*) FROM portal_mobile_otp_requests WHERE actor_id=2 AND purpose='create';", connection);
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        });
    }

    [MariaDbFact]
    public async Task IssuerActivatesOnlyConfirmedSubmissions()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            await Execute(connection, "INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            var settings = new GsmModemSettingsStore(configuration);
            var serial = new FakeSerial();
            var issuer = new FacultyOtpIssuer(store, settings, new GsmSmsTransport(_ => serial));
            Assert.Equal(new FacultyOtpIssueOutcome(FacultyOtpIssueResult.Issued, GsmSmsResult.Accepted, true),
                await issuer.IssueAsync("1000000001", "09123456789", secret));
            Assert.NotNull(serial.Code);
            Assert.True(await store.VerifyAsync("1000000001", serial.Code!, secret));

            var uncertain = new FakeSerial { FailAfterSubmission = true };
            issuer = new FacultyOtpIssuer(store, settings, new GsmSmsTransport(_ => uncertain));
            Assert.Equal(new FacultyOtpIssueOutcome(FacultyOtpIssueResult.DeliveryFailed, GsmSmsResult.DeliveryUnknown, true),
                await issuer.IssueAsync("1000000001", "09123456789", secret));
            Assert.NotNull(uncertain.Code);
            Assert.False(await store.VerifyAsync("1000000001", uncertain.Code!, secret));

            issuer = new FacultyOtpIssuer(store, settings, new GsmSmsTransport(_ => throw new UnauthorizedAccessException()));
            Assert.Equal(new FacultyOtpIssueOutcome(FacultyOtpIssueResult.DeliveryFailed, GsmSmsResult.Unavailable, true),
                await issuer.IssueAsync("1000000001", "09123456789", secret));
            await using var count = new MySqlCommand("SELECT COUNT(*) FROM faculty_otp_requests;", connection);
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        });
    }

    [MariaDbFact]
    public async Task SettingsFailureIsPropagatedAndInvalidatesReservation()
    {
        await WithDatabase(async (store, connection, secret) =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString
            }).Build();
            await Execute(connection, "DROP TABLE gsm_modem_settings;");
            var issuer = new FacultyOtpIssuer(store, new GsmModemSettingsStore(configuration),
                new GsmSmsTransport(_ => throw new InvalidOperationException("Must not open a port.")));
            await Assert.ThrowsAsync<MySqlException>(() => issuer.IssueAsync("1000000001", "09123456789", secret));
            await using var count = new MySqlCommand("SELECT COUNT(*) FROM faculty_otp_requests;", connection);
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        });
    }

    [MariaDbFact]
    public async Task ProtocolAuthenticatesChallengesAndSignsBoundResponses()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            await Execute(connection, "INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);");
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                    ["FacultyOtp:Secret"] = Convert.ToBase64String(secret),
                    ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master)
                }).Build();
                const string device = "1000000001";
                Assert.True((await new EnrollmentDeviceCredentialStore(configuration).StoreNewAsync(device, key, master)).Succeeded);
                var challenges = new EnrollmentChallengeStore(configuration);
                var serial = new FakeSerial();
                var protocol = new FacultyOtpProtocol(configuration, new GsmSmsTransport(_ => serial),
                    NullLogger<FacultyOtpProtocol>.Instance);
                async Task<(FacultyOtpRequest Request, byte[] Challenge)> MakeRequest(string operation, string value)
                {
                    var challenge = Assert.IsType<byte[]>((await challenges.IssueAsync(device, operation)).Challenge);
                    var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, device, operation, challenge, Encoding.ASCII.GetBytes(value + "\n"));
                    return (new FacultyOtpRequest(device, value, Convert.ToHexString(challenge).ToLowerInvariant(),
                        Convert.ToHexString(tag).ToLowerInvariant()), challenge);
                }
                void CheckResponse(FacultyOtpResponse response, byte[] challenge, string operation, string value, string status)
                {
                    var expected = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value + "\n"))).ToLowerInvariant() + "\n" + status + "\n";
                    Assert.Equal(expected, response.Body);
                    Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, device, operation, challenge,
                        Encoding.ASCII.GetBytes(response.Body), Convert.FromHexString(response.Tag)));
                }

                var issue = await MakeRequest("faculty_otp_request", "09123456789");
                Assert.Null(await protocol.ExecuteAsync("faculty_otp_request", issue.Request with { Value = "09999999999" }));
                Assert.Null(await protocol.ExecuteAsync("faculty_otp_verify", issue.Request with { Value = "123456" }));
                var response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_otp_request", issue.Request));
                CheckResponse(response, issue.Challenge, "faculty_otp_request", "09123456789", "processed");
                Assert.NotNull(serial.Code);
                Assert.Null(await protocol.ExecuteAsync("faculty_otp_request", issue.Request));

                var verification = await MakeRequest("faculty_otp_verify", serial.Code!);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_otp_verify", verification.Request));
                CheckResponse(response, verification.Challenge, "faculty_otp_verify", serial.Code!, "accepted");
                Assert.Null(await protocol.ExecuteAsync("faculty_otp_verify", verification.Request));
                var replay = await MakeRequest("faculty_otp_verify", serial.Code!);
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_otp_verify", replay.Request));
                CheckResponse(response, replay.Challenge, "faculty_otp_verify", serial.Code!, "denied");
                var unknown = await MakeRequest("faculty_otp_request", "09999999999");
                response = Assert.IsType<FacultyOtpResponse>(await protocol.ExecuteAsync("faculty_otp_request", unknown.Request));
                CheckResponse(response, unknown.Challenge, "faculty_otp_request", "09999999999", "processed");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
                CryptographicOperations.ZeroMemory(key);
            }
        });
    }

    [MariaDbFact]
    public async Task HttpRoutesAuthenticateAndVerifyUsingFakeModem()
    {
        await WithDatabase(async (_, connection, secret) =>
        {
            await Execute(connection, "INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate) VALUES (1, 'COM42', 9600);");
            var master = RandomNumberGenerator.GetBytes(32);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartClassMariaDb"] = connection.ConnectionString,
                    ["FacultyOtp:EnableDeviceApi"] = "true",
                    ["FacultyOtp:Secret"] = Convert.ToBase64String(secret),
                    ["EnrollmentSecurity:MasterKey"] = Convert.ToBase64String(master)
                });
                var serial = new FakeSerial();
                builder.Services.AddSingleton(new GsmSmsTransport(_ => serial));
                await using var app = builder.Build();
                app.MapFacultyOtpRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
                Assert.True((await new EnrollmentDeviceCredentialStore(app.Configuration).StoreNewAsync("1000000001", key, master)).Succeeded);
                await app.StartAsync();
                var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
                async Task<FacultyOtpRequest> MakeRequest(string path, string operation, string value)
                {
                    using var response = await client.PostAsJsonAsync($"api/device/faculty/otp/{path}/challenge", new FacultyOtpChallengeRequest("1000000001"));
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var challengeBody = await response.Content.ReadFromJsonAsync<ChallengeResponse>();
                    var challenge = Convert.FromHexString(challengeBody!.Challenge);
                    var tag = EnrollmentMessageAuthentication.ComputeTag(key, false, "1000000001", operation, challenge, Encoding.ASCII.GetBytes(value + "\n"));
                    return new FacultyOtpRequest("1000000001", value, challengeBody.Challenge, Convert.ToHexString(tag).ToLowerInvariant());
                }
                var issue = await MakeRequest("request", "faculty_otp_request", "09123456789");
                using var tampered = await client.PostAsJsonAsync("api/device/faculty/otp/request", issue with { Value = "09999999999" });
                Assert.Equal(HttpStatusCode.Forbidden, tampered.StatusCode);
                using var sent = await client.PostAsJsonAsync("api/device/faculty/otp/request", issue);
                Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
                Assert.NotNull(serial.Code);
                var verify = await MakeRequest("verify", "faculty_otp_verify", serial.Code!);
                using var result = await client.PostAsJsonAsync("api/device/faculty/otp/verify", verify);
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
                var signed = await result.Content.ReadFromJsonAsync<FacultyOtpResponse>();
                Assert.NotNull(signed);
                Assert.EndsWith("\naccepted\n", signed.Body);
                Assert.True(EnrollmentMessageAuthentication.VerifyTag(key, true, "1000000001", "faculty_otp_verify",
                    Convert.FromHexString(verify.Challenge!), Encoding.ASCII.GetBytes(signed.Body), Convert.FromHexString(signed.Tag)));
                using var replay = await client.PostAsJsonAsync("api/device/faculty/otp/verify", verify);
                Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
                CryptographicOperations.ZeroMemory(key);
            }
        });
    }

    private sealed record ChallengeResponse(string Challenge);

    private sealed class FakeSerial : IGsmSerialConnection
    {
        private readonly Queue<char> _input = new();
        public bool FailAfterSubmission { get; init; }
        public string? Code { get; private set; }
        public void Open() { }
        public void DiscardInput() => _input.Clear();
        public void Write(string value)
        {
            string response;
            if (value.StartsWith("AT+CMGS=", StringComparison.Ordinal)) response = "\r\n> ";
            else if (value.EndsWith('\u001a'))
            {
                var match = System.Text.RegularExpressions.Regex.Match(value, @"\b[0-9]{6}\b");
                Code = match.Success ? match.Value : null;
                response = FailAfterSubmission ? "" : "\r\n+CMGS: 1\r\nOK\r\n";
            }
            else response = "\r\nOK\r\n";
            foreach (var character in response) _input.Enqueue(character);
        }
        public int ReadChar() => _input.TryDequeue(out var value) ? value : throw new IOException("Simulated disconnection.");
        public void Dispose() { }
    }

    private static async Task WithDatabase(Func<FacultyOtpStore, MySqlConnection, byte[], Task> test)
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCLASS_TEST_CONNECTION")!)
        {
            Database = string.Empty
        };
        var database = "smartclass_otp_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await Execute(admin, $"CREATE DATABASE `{database}`;");
        try
        {
            builder.Database = database;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartClassMariaDb"] = builder.ConnectionString
            }).Build();
            var migrations = new DatabaseMigrationRunner(configuration, NullLogger<DatabaseMigrationRunner>.Instance);
            await migrations.RunMigrationsAsync();
            await migrations.RunMigrationsAsync();
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await Execute(connection, """
                INSERT INTO biometric_devices (id, device_id, display_name, is_active, paired_at)
                    VALUES (1, '1000000001', 'Device 1', 1, CURRENT_TIMESTAMP);
                INSERT INTO user_accounts (id, username, display_name, phone, password_hash, role, is_active)
                    VALUES (1, 'teacher', 'Teacher', '09123456789', 'hash', 'Teacher', 1);
                """);
            var secret = RandomNumberGenerator.GetBytes(32);
            try { await test(new FacultyOtpStore(configuration), connection, secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        finally
        {
            await Execute(admin, $"DROP DATABASE `{database}`;");
        }
    }

    private static async Task Execute(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
