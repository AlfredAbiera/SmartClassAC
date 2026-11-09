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
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Teacher, false)]
    public void PortalLoginPolicy_OnlyAllowsAdministrators(UserRole role, bool expected)
    {
        Assert.Equal(expected, PortalLoginPolicy.CanSignIn(role));
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

    [Theory]
    [InlineData("123456", true)]
    [InlineData("000001", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12345a", false)]
    [InlineData("１２３４５６", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MobileVerificationCode_RequiresExactlySixAsciiDigits(string? code, bool expected)
    {
        Assert.Equal(expected, MobileVerificationCodes.IsValid(code));
    }

    [Fact]
    public void TeacherRegistration_ValidatesLoginPasswordSeparatelyFromOtp()
    {
        var request = new CreateTeacherRequest("Teacher", "teacher", "EMP1", null,
            "09123456789", "ValidPassword1", null, "001234");

        Assert.True(TeacherAccountValidation.ValidateCreate(request).Succeeded);
        Assert.True(MobileVerificationCodes.IsValid(request.RegistrationOtp));
        Assert.Equal(PasswordPolicy.RequirementMessage,
            TeacherAccountValidation.ValidateCreate(request with { Password = "001234" }).Error);
        Assert.True(TeacherAccountValidation.ValidateCreate(request with { RegistrationOtp = "bad" }).Succeeded);
        Assert.False(MobileVerificationCodes.IsValid("bad"));
    }

    [Fact]
    public void TeacherUpdate_ValidatesOptionalPasswordBeforeVerification()
    {
        var request = new UpdateAdminTeacherRequest(1, "Teacher", "teacher", "EMP1", null,
            "09123456789", null, null, "123456");
        Assert.True(TeacherAccountValidation.ValidateUpdate(request).Succeeded);
        Assert.Equal(PasswordPolicy.RequirementMessage,
            TeacherAccountValidation.ValidateUpdate(request with { NewPassword = "123456" }).Error);
        Assert.True(TeacherAccountValidation.ValidateUpdate(request with { NewPassword = "ValidPassword1" }).Succeeded);
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
