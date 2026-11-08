using System.Security.Cryptography;

namespace SmartClassAC.Services;

/// <summary>
/// Authenticates a request using its scanner's stored key and consumes its challenge.
/// This does not authorize or perform an enrollment operation.
/// </summary>
internal sealed class EnrollmentDeviceAuthenticator
{
    private readonly EnrollmentDeviceCredentialStore _credentials;
    private readonly EnrollmentChallengeStore _challenges;

    public EnrollmentDeviceAuthenticator(IConfiguration configuration)
    {
        _credentials = new EnrollmentDeviceCredentialStore(configuration);
        _challenges = new EnrollmentChallengeStore(configuration);
    }

    public async Task<bool> VerifyAndConsumeAsync(
        string deviceId, string operation, byte[] masterKey, byte[] challenge, byte[] body, byte[] tag,
        CancellationToken cancellationToken = default)
    {
        var key = await _credentials.LoadAsync(deviceId, masterKey, cancellationToken);
        if (key is null) return false;
        try
        {
            return await _challenges.VerifyAndConsumeAsync(deviceId, operation, key, challenge, body, tag, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
