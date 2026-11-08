namespace SmartClassAC.Services;

internal enum FacultyOtpIssueResult
{
    Issued,
    Denied,
    DeliveryFailed
}

internal sealed record FacultyOtpIssueOutcome(FacultyOtpIssueResult Result, GsmSmsResult? TransportResult, bool ReservationUpdated);

/// <summary>Internal issuance only; callers must authenticate devices and normalize external responses.</summary>
internal sealed class FacultyOtpIssuer(
    FacultyOtpStore otpStore, GsmModemSettingsStore settingsStore, GsmSmsTransport transport)
{
    public async Task<FacultyOtpIssueOutcome> IssueAsync(
        string deviceId, string phone, byte[] secret, CancellationToken cancellationToken = default)
    {
        var reservation = await otpStore.ReserveAsync(deviceId, phone, secret, cancellationToken);
        if (reservation is null) return new(FacultyOtpIssueResult.Denied, null, false);

        var accepted = false;
        var updated = false;
        GsmSmsResult? transportResult = null;
        try
        {
            var settings = await settingsStore.GetAsync(cancellationToken);
            transportResult = await transport.SendOtpAsync(settings, phone, reservation.Code, cancellationToken);
            accepted = transportResult == GsmSmsResult.Accepted;
        }
        finally
        {
            updated = await otpStore.CompleteDeliveryAsync(deviceId, reservation.RequestId, accepted, CancellationToken.None);
        }
        return new(accepted && updated ? FacultyOtpIssueResult.Issued : FacultyOtpIssueResult.DeliveryFailed,
            transportResult, updated);
    }
}
