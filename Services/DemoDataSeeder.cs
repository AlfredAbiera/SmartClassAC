using Microsoft.Extensions.Options;

namespace SmartClassAC.Services;

/// <summary>
/// Seeds repeatable demo teachers, classrooms, and schedules for local testing.
/// See README.md (Demo / test data) for logins and cases. Idempotent: skips when EMP-1001 already exists.
/// </summary>
public sealed class DemoDataSeeder
{
    public const string MarkerEmployeeNumber = "EMP-1001";
    public const string SharedTeacherPassword = "TeacherDemo1!";

    private readonly IUserAccountRepository _accounts;
    private readonly PasswordHasher _passwordHasher;
    private readonly DemoDataOptions _options;
    private readonly ILogger<DemoDataSeeder> _logger;

    public DemoDataSeeder(
        IUserAccountRepository accounts,
        PasswordHasher passwordHasher,
        IOptions<DemoDataOptions> options,
        ILogger<DemoDataSeeder> logger)
    {
        _accounts = accounts;
        _passwordHasher = passwordHasher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var teachers = await _accounts.GetTeachersAsync(cancellationToken);
        if (teachers.Any(teacher => string.Equals(teacher.EmployeeNumber, MarkerEmployeeNumber, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("Demo data already present (employee {EmployeeNumber}); skipping seed.", MarkerEmployeeNumber);
            await EnsureDemoDevicePinsAsync(cancellationToken);
            return;
        }

        _logger.LogInformation("Seeding demo teachers, classrooms, and schedules for local testing.");

        var passwordHash = _passwordHasher.Hash(SharedTeacherPassword);

        foreach (var classroom in DemoClassrooms)
        {
            var created = await _accounts.CreateClassroomAsync(classroom, cancellationToken);
            if (!created.Succeeded)
            {
                _logger.LogWarning("Demo classroom '{Name}' was not created: {Error}", classroom.Name, created.Error);
            }
        }

        foreach (var teacher in DemoTeachers)
        {
            var created = await _accounts.CreateTeacherAsync(teacher, passwordHash, cancellationToken);
            if (!created.Succeeded)
            {
                _logger.LogWarning("Demo teacher '{Username}' was not created: {Error}", teacher.Username, created.Error);
            }
        }

        teachers = await _accounts.GetTeachersAsync(cancellationToken);
        var classrooms = await _accounts.GetClassroomsAsync(cancellationToken);

        long? IdOf(string employeeNumber) =>
            teachers.FirstOrDefault(teacher => string.Equals(teacher.EmployeeNumber, employeeNumber, StringComparison.OrdinalIgnoreCase))?.Id;

        long? Room(string name) =>
            classrooms.FirstOrDefault(room => string.Equals(room.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

        var mariaId = IdOf("EMP-1001");
        var johnId = IdOf("EMP-1002");
        var anaId = IdOf("EMP-1003");
        var room101 = Room("Room 101");
        var room202 = Room("Room 202");
        var scienceLab = Room("Science Lab");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        var tomorrow = today.AddDays(1);
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var windowStart = now.AddHours(-1);
        var windowEnd = now.AddHours(2);
        if (windowEnd <= windowStart)
        {
            windowStart = new TimeOnly(8, 0);
            windowEnd = new TimeOnly(17, 0);
        }

        var semesterResult = await _accounts.CreateSemesterAsync(
            new CreateSemesterRequest(
                $"Demo Semester {today:yyyy}",
                today.AddMonths(-1),
                today.AddMonths(4),
                IsActive: true),
            cancellationToken);
        if (!semesterResult.Succeeded)
        {
            _logger.LogWarning("Demo semester was not created: {Error}", semesterResult.Error);
        }

        var semesterId = (await _accounts.GetSemestersAsync(cancellationToken))
            .FirstOrDefault(item => item.Name.StartsWith("Demo Semester", StringComparison.Ordinal))?.Id;
        if (semesterId is long semester)
        {
            foreach (var teacherId in new[] { mariaId, johnId, anaId })
            {
                if (teacherId is long id)
                {
                    await _accounts.EnrollTeacherInSemesterAsync(id, semester, cancellationToken);
                }
            }

            var schedules = new List<CreateScheduleRequest>();
            if (mariaId is long maria && room101 is long r101)
            {
                schedules.Add(new CreateScheduleRequest(maria, r101, semester, ScheduleKinds.Regular, "Mathematics (in-window)", today, windowStart, windowEnd));
                schedules.Add(new CreateScheduleRequest(maria, r101, semester, ScheduleKinds.Regular, "Mathematics (tomorrow)", tomorrow, new TimeOnly(9, 0), new TimeOnly(10, 0)));
            }

            if (johnId is long john && room202 is long r202)
            {
                schedules.Add(new CreateScheduleRequest(john, r202, semester, ScheduleKinds.Regular, "English (in-window)", today, windowStart, windowEnd));
                schedules.Add(new CreateScheduleRequest(john, r202, semester, ScheduleKinds.Regular, "English (yesterday)", yesterday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
            }

            if (mariaId is long mariaScience && scienceLab is long lab)
            {
                schedules.Add(new CreateScheduleRequest(mariaScience, lab, semester, ScheduleKinds.Makeup, "Science Lab (makeup afternoon)", today, new TimeOnly(13, 0), new TimeOnly(14, 30)));
            }

            foreach (var schedule in schedules)
            {
                var created = await _accounts.CreateScheduleAsync(schedule, cancellationToken);
                if (!created.Succeeded)
                {
                    _logger.LogWarning("Demo schedule '{Subject}' was not created: {Error}", schedule.SubjectName, created.Error);
                }
            }
        }

        // Pre-enroll fingerprints for Maria so scan can be tested without a prior enroll call.
        if (mariaId is long mariaAccount)
        {
            await _accounts.EnrollFingerprintAsync(mariaAccount, "Right thumb", 1, cancellationToken);
            await _accounts.EnrollFingerprintAsync(mariaAccount, "Right index", 2, cancellationToken);
        }

        _logger.LogInformation(
            "Demo data ready. Teachers use password {Password}. See README.md for logins and cases.",
            SharedTeacherPassword);
        await EnsureDemoDevicePinsAsync(cancellationToken);
    }

    private async Task EnsureDemoDevicePinsAsync(CancellationToken cancellationToken)
    {
        foreach (var teacher in DemoTeachers)
        {
            if (string.IsNullOrWhiteSpace(teacher.DevicePin))
            {
                continue;
            }

            var result = await _accounts.SetTeacherDevicePinByEmployeeNumberAsync(
                teacher.EmployeeNumber,
                teacher.DevicePin,
                cancellationToken);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Demo PIN {Pin} for {Employee} was not applied: {Error}",
                    teacher.DevicePin,
                    teacher.EmployeeNumber,
                    result.Error);
            }
        }
    }

    private static readonly CreateClassroomRequest[] DemoClassrooms =
    {
        new("Room 101", 40, 24),
        new("Room 202", 35, 22),
        new("Science Lab", 28, 20),
        new("Computer Lab", 30, 23)
    };

    private static readonly CreateTeacherRequest[] DemoTeachers =
    {
        new("Maria Santos", "msantos", "EMP-1001", "maria.santos@school.test", SharedTeacherPassword, "1001"),
        new("John Reyes", "jreyes", "EMP-1002", "john.reyes@school.test", SharedTeacherPassword, "1002"),
        new("Ana Cruz", "acruz", "EMP-1003", "ana.cruz@school.test", SharedTeacherPassword, "1003")
    };
}
