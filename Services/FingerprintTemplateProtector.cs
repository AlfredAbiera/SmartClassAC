using System.Security.Cryptography;
using System.Text;

namespace SmartClassAC.Services;

public sealed class FingerprintTemplateProtector : IDisposable
{
    private const byte FormatVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;
    private const int MaxTemplateSize = 4096;
    private readonly byte[] _key;
    private bool _disposed;

    public FingerprintTemplateProtector(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("Fingerprint encryption requires a 32-byte key.", nameof(key));
        }

        _key = key.ToArray();
    }

    public byte[] Protect(long teacherId, Guid fingerprintId, ReadOnlySpan<byte> template)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateIdentity(teacherId, fingerprintId);
        if (template.IsEmpty || template.Length > MaxTemplateSize)
        {
            throw new ArgumentOutOfRangeException(nameof(template), "Fingerprint template length is invalid.");
        }

        var result = new byte[HeaderSize + template.Length];
        result[0] = FormatVersion;
        var nonce = result.AsSpan(1, NonceSize);
        var tag = result.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = result.AsSpan(HeaderSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, template, ciphertext, tag, AssociatedData(teacherId, fingerprintId));
        return result;
    }

    public byte[] Unprotect(long teacherId, Guid fingerprintId, ReadOnlySpan<byte> protectedTemplate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateIdentity(teacherId, fingerprintId);
        if (protectedTemplate.Length <= HeaderSize || protectedTemplate.Length > HeaderSize + MaxTemplateSize)
        {
            throw new ArgumentException("Protected fingerprint template length is invalid.", nameof(protectedTemplate));
        }

        if (protectedTemplate[0] != FormatVersion)
        {
            throw new ArgumentException("Unsupported protected fingerprint template format.", nameof(protectedTemplate));
        }

        var plaintext = new byte[protectedTemplate.Length - HeaderSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            protectedTemplate.Slice(1, NonceSize),
            protectedTemplate.Slice(HeaderSize),
            protectedTemplate.Slice(1 + NonceSize, TagSize),
            plaintext,
            AssociatedData(teacherId, fingerprintId));
        return plaintext;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }

    private static void ValidateIdentity(long teacherId, Guid fingerprintId)
    {
        if (teacherId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(teacherId));
        }

        if (fingerprintId == Guid.Empty)
        {
            throw new ArgumentException("Fingerprint ID is required.", nameof(fingerprintId));
        }
    }

    private static byte[] AssociatedData(long teacherId, Guid fingerprintId) =>
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"SmartClassAC:fingerprint:v1:{teacherId}:{fingerprintId:N}"));
}
