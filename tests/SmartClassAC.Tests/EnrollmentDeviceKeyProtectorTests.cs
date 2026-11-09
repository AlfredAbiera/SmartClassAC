using System.Security.Cryptography;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class EnrollmentDeviceKeyProtectorTests
{
    [Theory]
    [InlineData("The scanner is unknown, inactive, or unpaired.")]
    [InlineData("The scanner has an unresolved fingerprint write.")]
    [InlineData("EnrollmentSecurity:MasterKey must decode to 32 bytes.")]
    public void ProvisioningDiagnostic_ReportsKnownFailures(string message)
    {
        Assert.Contains(message, EnrollmentLocalProvisioning.DescribeFailure(new InvalidOperationException(message)));
    }

    [Fact]
    public void ProvisioningDiagnostic_DoesNotExposeRawExceptionDetails()
    {
        const string sensitiveDetail = "private configuration value";
        Assert.DoesNotContain(sensitiveDetail,
            EnrollmentLocalProvisioning.DescribeFailure(new CryptographicException(sensitiveDetail)));
        Assert.DoesNotContain(sensitiveDetail,
            EnrollmentLocalProvisioning.DescribeFailure(new InvalidOperationException(sensitiveDetail)));
        Assert.Contains("original master key",
            EnrollmentLocalProvisioning.DescribeFailure(new CryptographicException()));
    }

    [Fact]
    public void Protector_EncryptsWithUniqueNoncesAndBindsDevice()
    {
        var master = RandomNumberGenerator.GetBytes(32);
        var deviceKey = RandomNumberGenerator.GetBytes(32);
        using var protector = new EnrollmentDeviceKeyProtector(master);
        var first = protector.Protect("1000000001", deviceKey);
        var second = protector.Protect("1000000001", deviceKey);
        Assert.Equal(61, first.Length);
        Assert.NotEqual(first, second);
        Assert.Equal(deviceKey, protector.Unprotect("1000000001", first));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("1000000002", first));
        using var wrong = new EnrollmentDeviceKeyProtector(RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => wrong.Unprotect("1000000001", first));
        foreach (var index in new[] { 1, 13, 29 })
        {
            var modified = first.ToArray();
            modified[index] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("1000000001", modified));
        }
    }

    [Fact]
    public void Protector_RejectsInvalidInputAndUseAfterDisposal()
    {
        Assert.Throws<ArgumentException>(() => new EnrollmentDeviceKeyProtector(new byte[31]));
        var protector = new EnrollmentDeviceKeyProtector(new byte[32]);
        Assert.Throws<ArgumentException>(() => protector.Protect("bad", new byte[32]));
        Assert.Throws<ArgumentException>(() => protector.Protect("1", new byte[31]));
        Assert.Throws<CryptographicException>(() => protector.Unprotect("1", new byte[61]));
        var encrypted = protector.Protect("1", new byte[32]);
        protector.Dispose();
        protector.Dispose();
        Assert.Throws<ObjectDisposedException>(() => protector.Protect("1", new byte[32]));
        Assert.Throws<ObjectDisposedException>(() => protector.Unprotect("1", encrypted));
    }
}
