using Microsoft.Extensions.Options;
using MySqlConnector;

namespace SmartClassAC.Services;

internal sealed record FacultyDeviceSchedule(string Subject, string Classroom, TimeSpan Start, TimeSpan End);

/// <summary>Internal device-menu data; callers must authenticate the scanner before calling.</summary>
internal sealed class FacultyDeviceScheduleStore
{
    private readonly string _connectionString;
    private readonly SchoolTimeOptions _schoolTime;

    public FacultyDeviceScheduleStore(IConfiguration configuration, IOptions<SchoolTimeOptions> schoolTime)
    {
        _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before using the Faculty menu.");
        _schoolTime = schoolTime.Value;
    }

    public async Task<IReadOnlyList<FacultyDeviceSchedule>?> GetTodayAsync(
        string deviceId, string sessionToken, byte[] secret, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var teacherId = await FacultyOtpStore.LockSessionAsync(connection, transaction, deviceId, sessionToken, secret, cancellationToken);
        if (teacherId is null) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT subject_name, classroom_name, start_time, end_time
            FROM class_schedules
            WHERE teacher_account_id = @teacher AND schedule_date = @today
            ORDER BY start_time, id;
            """;
        command.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId.Value;
        command.Parameters.Add("@today", MySqlDbType.Date).Value = SchoolClock.Today(_schoolTime).ToDateTime(TimeOnly.MinValue);
        var schedules = new List<FacultyDeviceSchedule>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                schedules.Add(new FacultyDeviceSchedule(reader.GetString(0), reader.GetString(1), reader.GetTimeSpan(2), reader.GetTimeSpan(3)));
        }
        await transaction.CommitAsync(cancellationToken);
        return schedules;
    }
}
