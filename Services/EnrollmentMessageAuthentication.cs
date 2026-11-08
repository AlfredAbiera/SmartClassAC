using System.Security.Cryptography;
using System.Text;

namespace SmartClassAC.Services;

/// <summary>
/// Authenticates exact enrollment protocol messages; callers must enforce challenge freshness and single use.
/// </summary>
public static class EnrollmentMessageAuthentication
{
    public const int KeySize = 32;
    public const int ChallengeSize = 32;
    public const int TagSize = 32;
    public const int MaxBodySize = 4096;

    public static byte[] ComputeTag(
        ReadOnlySpan<byte> key, bool response, string deviceId, string operation,
        ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> body)
    {
        if (key.Length != KeySize)
            throw new ArgumentException("Enrollment authentication requires a 32-byte key.", nameof(key));
        if (challenge.Length != ChallengeSize)
            throw new ArgumentException("The challenge must be 32 bytes.", nameof(challenge));
        if (!IsValidDeviceId(deviceId))
            throw new ArgumentException("The device ID must contain 1 to 23 ASCII digits.", nameof(deviceId));
        if (!IsValidOperation(operation))
            throw new ArgumentException("The operation must contain lowercase ASCII letters, digits or underscores.", nameof(operation));
        if (body.Length > MaxBodySize)
            throw new ArgumentOutOfRangeException(nameof(body));

        var digest = SHA256.HashData(body);
        var canonical = string.Concat(
            "SmartClassAC-enroll-v1\n", response ? "response\n" : "request\n",
            deviceId, "\n", operation, "\n",
            Convert.ToHexString(challenge).ToLowerInvariant(), "\n",
            Convert.ToHexString(digest).ToLowerInvariant(), "\n");
        return HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(canonical));
    }

    public static bool VerifyTag(
        ReadOnlySpan<byte> key, bool response, string deviceId, string operation,
        ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> body, ReadOnlySpan<byte> tag)
    {
        if (tag.Length != TagSize)
            return false;
        var expected = ComputeTag(key, response, deviceId, operation, challenge, body);
        return CryptographicOperations.FixedTimeEquals(expected, tag);
    }

    private static bool IsOperationCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_';

    public static bool IsValidDeviceId(string? value) =>
        value is { Length: > 0 and <= 23 } && value.All(char.IsAsciiDigit);

    public static bool IsValidOperation(string? value) =>
        value is { Length: > 0 and <= 32 } && value.All(IsOperationCharacter);
}
