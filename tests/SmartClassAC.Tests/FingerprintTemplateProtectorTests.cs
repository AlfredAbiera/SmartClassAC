using System.Security.Cryptography;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class FingerprintTemplateProtectorTests
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Guid _fingerprintId = Guid.NewGuid();

    [Theory]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(4096)]
    public void Protect_RoundTripsAndUsesFreshNonce(int length)
    {
        var protector = new FingerprintTemplateProtector(_key);
        var template = RandomNumberGenerator.GetBytes(length);

        var first = protector.Protect(1, _fingerprintId, template);
        var second = protector.Protect(1, _fingerprintId, template);

        Assert.NotEqual(first, second);
        Assert.Equal(template, protector.Unprotect(1, _fingerprintId, first));
        Assert.Equal(template, protector.Unprotect(1, _fingerprintId, second));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(29)]
    public void Unprotect_RejectsTamperedNonceTagOrCiphertext(int index)
    {
        var protector = new FingerprintTemplateProtector(_key);
        var encrypted = protector.Protect(1, _fingerprintId, new byte[] { 1, 2, 3 });
        encrypted[index] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(1, _fingerprintId, encrypted));
    }

    [Fact]
    public void Unprotect_RejectsWrongIdentityAndKey()
    {
        var protector = new FingerprintTemplateProtector(_key);
        var encrypted = protector.Protect(1, _fingerprintId, new byte[] { 1 });

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(2, _fingerprintId, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(1, Guid.NewGuid(), encrypted));
        var other = new FingerprintTemplateProtector(RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(1, _fingerprintId, encrypted));
    }

    [Fact]
    public void Protector_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new FingerprintTemplateProtector(new byte[31]));
        var protector = new FingerprintTemplateProtector(_key);
        Assert.Throws<ArgumentOutOfRangeException>(() => protector.Protect(0, _fingerprintId, new byte[] { 1 }));
        Assert.Throws<ArgumentException>(() => protector.Protect(1, Guid.Empty, new byte[] { 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => protector.Protect(1, _fingerprintId, Array.Empty<byte>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => protector.Protect(1, _fingerprintId, new byte[4097]));
        Assert.Throws<ArgumentException>(() => protector.Unprotect(1, _fingerprintId, new byte[29]));
        var invalidVersion = protector.Protect(1, _fingerprintId, new byte[] { 1 });
        invalidVersion[0] = 2;
        Assert.Throws<ArgumentException>(() => protector.Unprotect(1, _fingerprintId, invalidVersion));
    }

    [Fact]
    public void Protector_CopiesKey()
    {
        var protector = new FingerprintTemplateProtector(_key);
        var encrypted = protector.Protect(1, _fingerprintId, new byte[] { 1 });
        Array.Clear(_key);

        Assert.Equal(new byte[] { 1 }, protector.Unprotect(1, _fingerprintId, encrypted));
    }

    [Fact]
    public void Protector_RejectsUseAfterDisposal()
    {
        var protector = new FingerprintTemplateProtector(_key);
        var encrypted = protector.Protect(1, _fingerprintId, new byte[] { 1 });
        protector.Dispose();
        protector.Dispose();

        Assert.Throws<ObjectDisposedException>(() => protector.Protect(1, _fingerprintId, new byte[] { 1 }));
        Assert.Throws<ObjectDisposedException>(() => protector.Unprotect(1, _fingerprintId, encrypted));
    }
}
