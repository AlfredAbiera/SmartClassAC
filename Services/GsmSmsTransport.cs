using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace SmartClassAC.Services;

internal enum GsmSmsResult
{
    Accepted,
    InvalidInput,
    NotConfigured,
    Unavailable,
    Rejected,
    DeliveryUnknown,
    Cancelled
}

internal interface IGsmSerialConnection : IDisposable
{
    void Open();
    void DiscardInput();
    void Write(string value);
    int ReadChar();
}

internal sealed class GsmSmsTransport
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<GsmModemSettings, IGsmSerialConnection> _createConnection;

    public GsmSmsTransport() : this(settings => new GsmSerialConnection(settings)) { }

    internal GsmSmsTransport(Func<GsmModemSettings, IGsmSerialConnection> createConnection)
    {
        _createConnection = createConnection;
    }

    public async Task<GsmSmsResult> SendOtpAsync(
        GsmModemSettings? settings, string phone, string code, CancellationToken cancellationToken = default)
    {
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
            return GsmSmsResult.InvalidInput;
        return await SendMessageAsync(settings, phone, "Your SmartClassAC verification code is " + code + ".", cancellationToken);
    }

    public Task<GsmSmsResult> SendTestAsync(
        GsmModemSettings? settings, string phone, CancellationToken cancellationToken = default) =>
        SendMessageAsync(settings, phone, "SmartClassAC modem test message.", cancellationToken);

    internal static bool IsAsciiMobile(string? phone) =>
        phone is { Length: 11 } && phone.StartsWith("09", StringComparison.Ordinal) && phone.All(char.IsAsciiDigit);

    private async Task<GsmSmsResult> SendMessageAsync(
        GsmModemSettings? settings, string phone, string message, CancellationToken cancellationToken)
    {
        if (settings is null) return GsmSmsResult.NotConfigured;
        if (!IsValidSettings(settings) || !IsAsciiMobile(phone))
            return GsmSmsResult.InvalidInput;

        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return GsmSmsResult.Cancelled;
        }

        try
        {
            return await Task.Run(() => Send(settings, phone, message, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private GsmSmsResult Send(GsmModemSettings settings, string phone, string message, CancellationToken cancellationToken)
    {
        var submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var serial = _createConnection(settings);
            serial.Open();
            serial.DiscardInput();
            foreach (var command in new[] { "AT\r", "ATE0\r", "AT+CMGF=1\r", "AT+CSCS=\"GSM\"\r" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                serial.Write(command);
                if (!WaitForResponse(serial, false, false, TimeSpan.FromSeconds(5), cancellationToken))
                    return GsmSmsResult.Rejected;
            }

            try
            {
                serial.Write("AT+CMGS=\"+63" + phone[1..] + "\"\r");
                if (!WaitForResponse(serial, true, false, TimeSpan.FromSeconds(5), cancellationToken))
                    return GsmSmsResult.Rejected;
                cancellationToken.ThrowIfCancellationRequested();
                submitted = true;
                serial.Write(message + "\u001a");
                return WaitForResponse(serial, false, true, TimeSpan.FromSeconds(45), cancellationToken)
                    ? GsmSmsResult.Accepted : GsmSmsResult.DeliveryUnknown;
            }
            finally
            {
                if (!submitted) serial.Write("\u001b");
            }
        }
        catch (OperationCanceledException)
        {
            return submitted ? GsmSmsResult.DeliveryUnknown : GsmSmsResult.Cancelled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or TimeoutException or NotSupportedException)
        {
            return submitted ? GsmSmsResult.DeliveryUnknown : GsmSmsResult.Unavailable;
        }
    }

    private static bool WaitForResponse(IGsmSerialConnection serial, bool prompt, bool requireMessageReference,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var line = new StringBuilder();
        var hasMessageReference = false;
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int value;
            try { value = serial.ReadChar(); }
            catch (TimeoutException) { continue; }
            if (value < 0) continue;
            var character = (char)value;
            if (prompt && character == '>' && line.Length == 0) return true;
            if (character is '\r' or '\n')
            {
                if (line.Length == 0) continue;
                var response = line.ToString().Trim();
                line.Clear();
                if (response == "ERROR" || response.StartsWith("+CMS ERROR:", StringComparison.Ordinal) ||
                    response.StartsWith("+CME ERROR:", StringComparison.Ordinal))
                    return false;
                if (response.StartsWith("+CMGS:", StringComparison.Ordinal))
                {
                    var reference = response.AsSpan("+CMGS:".Length).Trim();
                    hasMessageReference = reference.Length > 0;
                    foreach (var digit in reference)
                        if (!char.IsAsciiDigit(digit)) hasMessageReference = false;
                }
                if (!prompt && response == "OK")
                    return !requireMessageReference || hasMessageReference;
            }
            else
            {
                if (line.Length >= 1024) return false;
                line.Append(character);
            }
        }
        throw new TimeoutException("The modem did not respond within the allotted time.");
    }

    private static bool IsValidSettings(GsmModemSettings settings) =>
        settings.PortName is { Length: > 3 and <= 12 } &&
        settings.PortName.StartsWith("COM", StringComparison.Ordinal) &&
        settings.PortName[3] is >= '1' and <= '9' &&
        settings.PortName.AsSpan(3).ToString().All(char.IsAsciiDigit) &&
        GsmModemSettingsStore.BaudRates.Contains(settings.BaudRate);
}

internal sealed class GsmSerialConnection : IGsmSerialConnection
{
    private readonly SerialPort _port;

    public GsmSerialConnection(GsmModemSettings settings)
    {
        _port = new SerialPort(settings.PortName, settings.BaudRate)
        {
            Encoding = Encoding.ASCII,
            ReadTimeout = 250,
            WriteTimeout = 2000,
            NewLine = "\r\n",
            DtrEnable = false,
            RtsEnable = false
        };
    }

    public void Open() => _port.Open();
    public void DiscardInput() => _port.DiscardInBuffer();
    public void Write(string value) => _port.Write(value);
    public int ReadChar() => _port.ReadChar();
    public void Dispose() => _port.Dispose();
}
