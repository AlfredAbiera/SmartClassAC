using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class GsmSmsTransportTests
{
    private static readonly GsmModemSettings Settings = new("COM42", 9600);

    [Fact]
    public async Task SendsOnlyAfterPromptAndRequiresModemAcceptance()
    {
        var serial = new FakeSerial();
        var transport = new GsmSmsTransport(_ => serial);

        var result = await transport.SendOtpAsync(Settings, "09123456789", "123456");

        Assert.Equal(GsmSmsResult.Accepted, result);
        Assert.Equal(new[]
        {
            "AT\r", "ATE0\r", "AT+CMGF=1\r", "AT+CSCS=\"GSM\"\r", "AT+CMGS=\"+639123456789\"\r",
            "Your SmartClassAC verification code is 123456.\u001a"
        }, serial.Writes);
        Assert.True(serial.Disposed);
    }

    [Fact]
    public async Task TestSmsUsesFixedMessageAndNoOtp()
    {
        var serial = new FakeSerial();
        var result = await new GsmSmsTransport(_ => serial).SendTestAsync(Settings, "09123456789");
        Assert.Equal(GsmSmsResult.Accepted, result);
        Assert.Equal("AT+CMGS=\"+639123456789\"\r", serial.Writes[^2]);
        Assert.Equal("SmartClassAC modem test message.\u001a", serial.Writes[^1]);
    }

    [Fact]
    public async Task RegistrationAcknowledgmentIncludesEmployeeNumberWithoutCredentials()
    {
        var serial = new FakeSerial();
        var result = await new GsmSmsTransport(_ => serial)
            .SendRegistrationAcknowledgmentAsync(Settings, "09123456789", "EMP-000001");
        Assert.Equal(GsmSmsResult.Accepted, result);
        Assert.Equal("You are successfully registered in SmartClassAC. Your employee number is EMP-000001.\u001a", serial.Writes[^1]);
    }

    [Fact]
    public async Task AttendanceReminderSmsExplainsAutomaticMissingTimeOut()
    {
        var serial = new FakeSerial();
        var result = await new GsmSmsTransport(_ => serial)
            .SendAttendanceReminderAsync(Settings, "09123456789");

        Assert.Equal(GsmSmsResult.Accepted, result);
        Assert.Equal(
            "Attendance reminder: your class has ended. No time-out was recorded, so you were marked Missing time-out. Contact your administrator if this is incorrect.\u001a",
            serial.Writes[^1]);
    }

    [Fact]
    public async Task SubstitutionOtpIdentifiesTheClassDateAndRequestsAgreement()
    {
        var serial = new FakeSerial();
        var result = await new GsmSmsTransport(_ => serial).SendSubstitutionOtpAsync(
            Settings, "09123456789", "123456", new DateOnly(2026, 11, 24), new TimeOnly(9, 30));

        Assert.Equal(GsmSmsResult.Accepted, result);
        Assert.Equal("SmartClassAC substitute request for 2026-11-24 at 09:30. Give the admin this code only if you agree: 123456.\u001a", serial.Writes[^1]);
    }

    [Theory]
    [InlineData(true, "time-in")]
    [InlineData(false, "time-out")]
    public async Task AttendanceOtpIdentifiesActionAndClassTime(bool timeIn, string action)
    {
        var serial = new FakeSerial();
        var result = await new GsmSmsTransport(_ => serial).SendAttendanceOtpAsync(
            Settings, "09123456789", "123456", new DateOnly(2026, 11, 24), new TimeOnly(9, 30), timeIn);
        Assert.Equal(GsmSmsResult.Accepted, result);
        Assert.Contains($"{action} for 2026-11-24 at 09:30", serial.Writes[^1]);
        Assert.Contains("123456", serial.Writes[^1]);
    }

    [Fact]
    public async Task PasswordResetSmsIncludesOneUseResetLinkAndExpiry()
    {
        var serial = new FakeSerial();
        var resetUrl = "https://school.example/reset-password?token=secure-token";
        var result = await new GsmSmsTransport(_ => serial)
            .SendPasswordResetAsync(Settings, "09123456789", resetUrl);

        Assert.Equal(GsmSmsResult.Accepted, result);
        Assert.Equal(
            $"Reset your SmartClassAC password: {resetUrl} This link expires in 15 minutes and can be used once.\u001a",
            serial.Writes[^1]);
    }

    [Theory]
    [InlineData("0912345678\r")]
    [InlineData("09١٢٣٤٥٦٧٨٩")]
    [InlineData("0912345678")]
    public async Task TestSmsRejectsInvalidNumberWithoutOpeningPort(string phone)
    {
        var opened = false;
        var transport = new GsmSmsTransport(_ => { opened = true; return new FakeSerial(); });
        Assert.Equal(GsmSmsResult.InvalidInput, await transport.SendTestAsync(Settings, phone));
        Assert.False(opened);
    }

    [Fact]
    public async Task TestSmsReportsUncertainSubmission()
    {
        var serial = new FakeSerial { SubmissionResponse = "" };
        var result = await new GsmSmsTransport(_ => serial).SendTestAsync(Settings, "09123456789");
        Assert.Equal(GsmSmsResult.DeliveryUnknown, result);
    }

    [Theory]
    [InlineData("OK\r\n")]
    [InlineData("+CMGS: x\r\nOK\r\n")]
    [InlineData("+CMS ERROR: 500\r\n")]
    public async Task DoesNotClaimSuccessWithoutMessageReferenceAndOk(string response)
    {
        var serial = new FakeSerial { SubmissionResponse = response };
        var result = await new GsmSmsTransport(_ => serial).SendOtpAsync(Settings, "09123456789", "123456");
        Assert.Equal(GsmSmsResult.DeliveryUnknown, result);
    }

    [Fact]
    public async Task CommunicationFailureAfterSubmissionIsUncertain()
    {
        var serial = new FakeSerial { SubmissionResponse = "" };
        var result = await new GsmSmsTransport(_ => serial).SendOtpAsync(Settings, "09123456789", "123456");
        Assert.Equal(GsmSmsResult.DeliveryUnknown, result);
    }

    [Fact]
    public async Task RejectedPromptAbortsMessageEntryWithoutSending()
    {
        var serial = new FakeSerial { PromptResponse = "\r\nERROR\r\n" };
        var result = await new GsmSmsTransport(_ => serial).SendOtpAsync(Settings, "09123456789", "123456");
        Assert.Equal(GsmSmsResult.Rejected, result);
        Assert.Equal("\u001b", serial.Writes.Last());
        Assert.DoesNotContain(serial.Writes, value => value.Contains('\u001a'));
    }

    [Fact]
    public async Task CancellationAtPromptAbortsWithoutSubmitting()
    {
        using var cancellation = new CancellationTokenSource();
        var serial = new FakeSerial { AfterPrompt = cancellation.Cancel };
        var result = await new GsmSmsTransport(_ => serial).SendOtpAsync(Settings, "09123456789", "123456", cancellation.Token);
        Assert.Equal(GsmSmsResult.Cancelled, result);
        Assert.Equal("\u001b", serial.Writes.Last());
    }

    [Fact]
    public async Task CancellationAfterSubmissionIsUncertain()
    {
        using var cancellation = new CancellationTokenSource();
        var serial = new FakeSerial { AfterSubmission = cancellation.Cancel };
        var result = await new GsmSmsTransport(_ => serial).SendOtpAsync(Settings, "09123456789", "123456", cancellation.Token);
        Assert.Equal(GsmSmsResult.DeliveryUnknown, result);
    }

    [Fact]
    public async Task ConcurrentSendsDoNotOpenPortAtTheSameTime()
    {
        using var release = new ManualResetEventSlim();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeSerial { AfterSubmission = () => { submitted.SetResult(); release.Wait(); } };
        var count = 0;
        var transport = new GsmSmsTransport(_ =>
        {
            if (Interlocked.Increment(ref count) == 1) return first;
            secondOpened.SetResult();
            return new FakeSerial();
        });
        var firstSend = transport.SendOtpAsync(Settings, "09123456789", "123456");
        try
        {
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondSend = transport.SendTestAsync(Settings, "09123456789");
            Assert.NotSame(secondOpened.Task, await Task.WhenAny(secondOpened.Task, Task.Delay(100)));
            release.Set();
            Assert.Equal(GsmSmsResult.Accepted, await firstSend);
            Assert.Equal(GsmSmsResult.Accepted, await secondSend);
            Assert.True(secondOpened.Task.IsCompletedSuccessfully);
        }
        finally
        {
            release.Set();
        }
    }

    [Theory]
    [InlineData("09123456789", "12\r456")]
    [InlineData("0912345678\r", "123456")]
    [InlineData("09١٢٣٤٥٦٧٨٩", "123456")]
    [InlineData("09123456789", "12345")]
    public async Task InvalidInputNeverOpensPort(string phone, string code)
    {
        var opened = false;
        var transport = new GsmSmsTransport(_ => { opened = true; return new FakeSerial(); });
        Assert.Equal(GsmSmsResult.InvalidInput, await transport.SendOtpAsync(Settings, phone, code));
        Assert.False(opened);
    }

    [Fact]
    public async Task MissingSettingsAndUnavailablePortAreExplicit()
    {
        var transport = new GsmSmsTransport(_ => throw new UnauthorizedAccessException());
        Assert.Equal(GsmSmsResult.NotConfigured, await transport.SendOtpAsync(null, "09123456789", "123456"));
        Assert.Equal(GsmSmsResult.Unavailable, await transport.SendOtpAsync(Settings, "09123456789", "123456"));
    }

    private sealed class FakeSerial : IGsmSerialConnection
    {
        private readonly Queue<char> _input = new();
        public readonly List<string> Writes = [];
        public string PromptResponse { get; init; } = "\r\n> ";
        public string SubmissionResponse { get; init; } = "\r\n+CMGS: 23\r\n\r\nOK\r\n";
        public Action? AfterPrompt { get; init; }
        public Action? AfterSubmission { get; init; }
        public bool Disposed { get; private set; }

        public void Open() { }
        public void DiscardInput() => _input.Clear();
        public void Write(string value)
        {
            Writes.Add(value);
            var response = value.StartsWith("AT+CMGS=", StringComparison.Ordinal) ? PromptResponse :
                value.EndsWith('\u001a') ? SubmissionResponse :
                value == "\u001b" ? "" : "\r\nOK\r\n";
            foreach (var character in response) _input.Enqueue(character);
            if (value.EndsWith('\u001a')) AfterSubmission?.Invoke();
        }

        public int ReadChar()
        {
            if (!_input.TryDequeue(out var value)) throw new IOException("Simulated disconnected modem.");
            if (value == '>') AfterPrompt?.Invoke();
            return value;
        }

        public void Dispose() => Disposed = true;
    }
}
