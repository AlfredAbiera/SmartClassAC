using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MySqlConnector;

namespace SmartClassAC.Services;

public sealed record GsmModemSettings(string PortName, int BaudRate);

public sealed class GsmModemSettingsStore
{
    public static readonly int[] BaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];
    private readonly string _connectionString;

    public GsmModemSettingsStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SmartClassMariaDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:SmartClassMariaDb before configuring the modem.");
    }

    public static IReadOnlyList<string> GetPorts()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("COM port discovery is available only on Windows.");
        return GetWindowsPorts();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> GetWindowsPorts()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (key is null) return Array.Empty<string>();
        return key.GetValueNames()
            .Select(name => key.GetValue(name) as string)
            .Where(name => name is not null && Regex.IsMatch(name, @"^COM[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(name => name!.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name.Length).ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<GsmModemSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT port_name, baud_rate FROM gsm_modem_settings WHERE singleton_id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new GsmModemSettings(reader.GetString(0), reader.GetInt32(1)) : null;
    }

    public async Task<OperationResult> SaveAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
    {
        if (!BaudRates.Contains(baudRate) || !GetPorts().Contains(portName, StringComparer.Ordinal))
            return OperationResult.Failure("Select an available COM port and a supported baud rate.");
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gsm_modem_settings (singleton_id, port_name, baud_rate)
            VALUES (1, @port, @baud)
            ON DUPLICATE KEY UPDATE port_name = VALUES(port_name), baud_rate = VALUES(baud_rate), updated_utc = UTC_TIMESTAMP(6);
            """;
        command.Parameters.Add("@port", MySqlDbType.VarChar).Value = portName;
        command.Parameters.Add("@baud", MySqlDbType.Int32).Value = baudRate;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return OperationResult.Success();
    }
}
