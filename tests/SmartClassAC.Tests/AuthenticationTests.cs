using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public void PasswordHasher_RoundTripsPassword()
    {
        var hasher = new PasswordHasher();
        var hash = hasher.Hash("CorrectPassword1");

        Assert.True(hasher.Verify("CorrectPassword1", hash));
        Assert.False(hasher.Verify("WrongPassword1", hash));
    }

    [Fact]
    public void PasswordHasher_UsesDifferentSaltForEachHash()
    {
        var hasher = new PasswordHasher();

        Assert.NotEqual(hasher.Hash("SamePassword1"), hasher.Hash("SamePassword1"));
    }

    [Fact]
    public void PasswordHasher_RejectsEmptyPassword()
    {
        var hasher = new PasswordHasher();
        var hash = hasher.Hash("NonEmptyPassword1");

        Assert.False(hasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void PasswordHasher_RejectsMalformedHash()
    {
        var hasher = new PasswordHasher();

        Assert.False(hasher.Verify("anything", "not-a-pbkdf2-hash"));
        Assert.False(hasher.Verify("anything", "PBKDF2-SHA256$bad$salt$key"));
    }

    [Fact]
    public void PasswordHasher_RejectsHashWithInvalidKeyLength()
    {
        var hasher = new PasswordHasher();

        Assert.False(hasher.Verify("anything", "PBKDF2-SHA256$210000$AQ==$AQ=="));
    }

    [Theory]
    [InlineData("Short1", false)]
    [InlineData("lowercaseonly1", false)]
    [InlineData("UPPERCASEONLY1", false)]
    [InlineData("ValidPassword1", true)]
    [InlineData("Valid Pass1word", false)]
    public void PasswordPolicy_EnforcesRequirements(string password, bool expected)
    {
        Assert.Equal(expected, PasswordPolicy.MeetsRequirements(password));
    }

    [Fact]
    public void LoginAttemptLimiter_BlocksAfterFiveFailuresAndClearsOnSuccess()
    {
        var limiter = new LoginAttemptLimiter();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            limiter.RecordFailure(" Admin ");
        }

        Assert.True(limiter.IsBlocked("admin"));

        limiter.RecordSuccess("ADMIN");

        Assert.False(limiter.IsBlocked("admin"));
    }

    [Fact]
    public void LoginAttemptLimiter_IsolatesIdentifiers()
    {
        var limiter = new LoginAttemptLimiter();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            limiter.RecordFailure("admin");
        }

        Assert.True(limiter.IsBlocked("admin"));
        Assert.False(limiter.IsBlocked("teacher"));
    }

    [Fact]
    public void LoginAttemptLimiter_NormalizesWhitespaceAndCase()
    {
        var limiter = new LoginAttemptLimiter();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            limiter.RecordFailure(" Admin ");
        }

        Assert.True(limiter.IsBlocked("ADMIN"));
    }
}
