using MySqlConnector;

namespace SmartClassAC.Services;

internal static class EnrollmentRetirementCheck
{
    internal static async Task VerifyDeletedAsync(IConfiguration configuration, string deviceId, string database)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId) || string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("Specify the old numeric device ID and exact database name.");
        var builder = new MySqlConnectionStringBuilder(configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Configure the intended portal database."));
        if (!string.Equals(builder.Database, database, StringComparison.Ordinal))
            throw new InvalidOperationException("Configured database does not match the explicitly selected database.");
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM biometric_devices WHERE device_id=@device;";
        command.Parameters.Add("@device", MySqlDbType.VarChar).Value = deviceId;
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException("The old device still exists. Use its managed decommissioning process, not orphan retirement.");
        command.CommandText = """
            SELECT COUNT(*) FROM fingerprint_enrollment_write_guards g
            LEFT JOIN fingerprint_enrollment_preparations p ON p.id=g.preparation_id
            LEFT JOIN biometric_devices d ON d.id=p.device_id
            WHERE p.id IS NULL OR d.id IS NULL;
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException("Orphaned fingerprint guards exist. Investigate before retirement.");
        command.CommandText = """
            SELECT COUNT(*) FROM ac_ir_state s
            LEFT JOIN biometric_devices d ON d.id=s.device_id
            WHERE d.id IS NULL AND s.state='inflight';
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException("Orphaned in-flight IR commands exist. Investigate before retirement.");
        command.CommandText = """
            SELECT COUNT(*) FROM fingerprint_enrollment_device_credentials c
            LEFT JOIN biometric_devices d ON d.id=c.device_id WHERE d.id IS NULL;
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException("Orphaned scanner credentials exist. Investigate before retirement.");
    }

    internal static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        if (args.Length != 3 || !Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Use the local USB retirement tool with old device ID and database name.");
            return 1;
        }
        try
        {
            await VerifyDeletedAsync(configuration, args[1], args[2]);
            Console.WriteLine($"SC-RETIRE-CHECK 1 {args[1]}");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or MySqlException or ArgumentException)
        {
            Console.Error.WriteLine(exception is MySqlException
                ? "Retirement database check failed. Verify configuration, schema and database access."
                : exception.Message);
            return 1;
        }
    }
}
