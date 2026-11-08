using System.Security.Cryptography;
using System.Text;

namespace SmartClassAC.Services;

internal sealed class EnrollmentDeviceKeyProtector : IDisposable
{
    private const byte Version = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int ProtectedLength = 1 + NonceLength + TagLength + EnrollmentMessageAuthentication.KeySize;
    private readonly byte[] _masterKey;
    private bool _disposed;

    public EnrollmentDeviceKeyProtector(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != EnrollmentMessageAuthentication.KeySize)
            throw new ArgumentException("The enrollment key protection key must be 32 bytes.", nameof(masterKey));
        _masterKey = masterKey.ToArray();
    }

    public byte[] Protect(string deviceId, ReadOnlySpan<byte> deviceKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDeviceId(deviceId);
        if (deviceKey.Length != EnrollmentMessageAuthentication.KeySize)
            throw new ArgumentException("The device key must be 32 bytes.", nameof(deviceKey));

        var result = new byte[ProtectedLength];
        result[0] = Version;
        var nonce = result.AsSpan(1, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_masterKey, TagLength);
        aes.Encrypt(nonce, deviceKey, result.AsSpan(1 + NonceLength + TagLength),
            result.AsSpan(1 + NonceLength, TagLength), AssociatedData(deviceId));
        return result;
    }

    public byte[] Unprotect(string deviceId, ReadOnlySpan<byte> protectedKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDeviceId(deviceId);
        if (protectedKey.Length != ProtectedLength || protectedKey[0] != Version)
            throw new CryptographicException("Invalid or unsupported protected enrollment key.");
        var key = new byte[EnrollmentMessageAuthentication.KeySize];
        try
        {
            using var aes = new AesGcm(_masterKey, TagLength);
            aes.Decrypt(protectedKey.Slice(1, NonceLength), protectedKey.Slice(1 + NonceLength + TagLength),
                protectedKey.Slice(1 + NonceLength, TagLength), key, AssociatedData(deviceId));
            return key;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_masterKey);
        _disposed = true;
    }

    private static void ValidateDeviceId(string deviceId)
    {
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(deviceId))
            throw new ArgumentException("The device ID must contain 1 to 23 ASCII digits.", nameof(deviceId));
    }

    private static byte[] AssociatedData(string deviceId) =>
        Encoding.ASCII.GetBytes("SmartClassAC:enrollment-device-key:v1:" + deviceId);
}
