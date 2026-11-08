using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class DeviceDeletionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.Teacher)]
    public async Task DeleteDevice_RejectsNonAdmins(UserRole? role)
    {
        var (service, repository) = CreateService(role);
        if (role is not null)
        {
            await service.RestoreUserAsync(1);
        }

        var result = await service.DeleteBiometricDeviceAsync(7);

        Assert.False(result.Succeeded);
        Assert.Null(repository.DeletedId);
    }

    [Fact]
    public async Task DeleteDevice_RejectsInvalidId()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);

        var result = await service.DeleteBiometricDeviceAsync(0);

        Assert.False(result.Succeeded);
        Assert.Null(repository.DeletedId);
    }

    [Fact]
    public async Task DeleteDevice_AdminDelegatesAndSurfacesFailure()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        repository.DeleteResult = OperationResult.Failure("Active session.");
        await service.RestoreUserAsync(1);

        var result = await service.DeleteBiometricDeviceAsync(7);

        Assert.Equal(7, repository.DeletedId);
        Assert.False(result.Succeeded);
        Assert.Equal("Active session.", result.Error);
    }

    [Fact]
    public async Task DeleteDevice_AdminCanDelete()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);

        var result = await service.DeleteBiometricDeviceAsync(7);

        Assert.True(result.Succeeded);
        Assert.Equal(7, repository.DeletedId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.Teacher)]
    public async Task SelectEnrollmentScanner_RejectsNonAdmins(UserRole? role)
    {
        var (service, repository) = CreateService(role);
        if (role is not null) await service.RestoreUserAsync(1);

        var result = await service.SetEnrollmentScannerAsync(7);

        Assert.False(result.Succeeded);
        Assert.Equal(0, repository.SelectionCalls);
    }

    [Fact]
    public async Task SelectEnrollmentScanner_RejectsInvalidId()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);

        var result = await service.SetEnrollmentScannerAsync(0);

        Assert.False(result.Succeeded);
        Assert.Equal(0, repository.SelectionCalls);
    }

    [Fact]
    public async Task SelectEnrollmentScanner_AdminCanSelectAndClear()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);

        Assert.True((await service.SetEnrollmentScannerAsync(7)).Succeeded);
        Assert.Equal(7, repository.SelectedScannerId);
        Assert.True((await service.SetEnrollmentScannerAsync(null)).Succeeded);
        Assert.Null(repository.SelectedScannerId);
        Assert.Equal(2, repository.SelectionCalls);
    }

    [Fact]
    public async Task SelectEnrollmentScanner_SurfacesRepositoryFailure()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        repository.SelectionResult = OperationResult.Failure("Scanner unavailable.");
        await service.RestoreUserAsync(1);

        var result = await service.SetEnrollmentScannerAsync(7);

        Assert.False(result.Succeeded);
        Assert.Equal("Scanner unavailable.", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.Teacher)]
    public async Task BlockFingerprintSlot_RejectsNonAdmins(UserRole? role)
    {
        var (service, repository) = CreateService(role);
        if (role is not null) await service.RestoreUserAsync(1);

        var result = await service.SetFingerprintSlotBlockedAsync(2, true);

        Assert.False(result.Succeeded);
        Assert.Equal(0, repository.SlotBlockCalls);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetBlockedFingerprintSlotsAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task BlockFingerprintSlot_RejectsOutOfRangeSlots(long slot)
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);

        Assert.False((await service.SetFingerprintSlotBlockedAsync(slot, true)).Succeeded);
        Assert.Equal(0, repository.SlotBlockCalls);
    }

    [Fact]
    public async Task BlockFingerprintSlot_AdminDelegatesAndSurfacesFailure()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);
        repository.SlotBlockResult = OperationResult.Failure("Database rejected the block.");

        var result = await service.SetFingerprintSlotBlockedAsync(2, true);

        Assert.False(result.Succeeded);
        Assert.Equal("Database rejected the block.", result.Error);
        Assert.Equal(2, repository.BlockedSlot);
        Assert.True(repository.BlockedValue);
        Assert.Equal(new long[] { 2 }, await service.GetBlockedFingerprintSlotsAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.Teacher)]
    public async Task InspectFingerprintSlot_RejectsNonAdmins(UserRole? role)
    {
        var (service, repository) = CreateService(role);
        if (role is not null) await service.RestoreUserAsync(1);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.InspectFingerprintSlotAsync(2));
        Assert.Equal(0, repository.SlotInspectionCalls);
    }

    [Fact]
    public async Task InspectFingerprintSlot_ValidatesAndDelegatesForAdmin()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.InspectFingerprintSlotAsync(0));
        Assert.Equal(0, repository.SlotInspectionCalls);
        var inspection = await service.InspectFingerprintSlotAsync(2);
        Assert.Equal(2, inspection.Slot);
        Assert.True(inspection.IsBlocked);
        Assert.Equal("Teacher", inspection.TeacherName);
        Assert.Equal(1, repository.SlotInspectionCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.Teacher)]
    public async Task PrepareEnrollment_RequiresAdmin(UserRole? role)
    {
        var (service, repository) = CreateService(role);
        if (role is not null) await service.RestoreUserAsync(1);
        Assert.False((await service.PrepareFingerprintEnrollmentAsync(2, "Right thumb", 2)).Succeeded);
        Assert.False((await service.PrepareFingerprintEnrollmentAutomaticallyAsync(2, "Right thumb")).Succeeded);
        Assert.False((await service.CancelFingerprintEnrollmentPreparationAsync(Guid.NewGuid().ToString("D"))).Succeeded);
        Assert.False((await service.AuthorizeFingerprintEnrollmentAsync(Guid.NewGuid().ToString("D"), true)).Succeeded);
        Assert.False((await service.RevokeFingerprintEnrollmentAuthorizationAsync(Guid.NewGuid().ToString("D"))).Succeeded);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetFingerprintEnrollmentPreparationsAsync());
        Assert.Equal(0, repository.PreparationCalls);
    }

    [Fact]
    public async Task PrepareEnrollment_ValidatesAndDelegates()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);
        Assert.False((await service.PrepareFingerprintEnrollmentAsync(0, "Right thumb", 2)).Succeeded);
        Assert.False((await service.PrepareFingerprintEnrollmentAsync(1, "other", 2)).Succeeded);
        Assert.False((await service.PrepareFingerprintEnrollmentAsync(1, "Right thumb", 65536)).Succeeded);
        Assert.False((await service.PrepareFingerprintEnrollmentAutomaticallyAsync(0, "Right thumb")).Succeeded);
        Assert.False((await service.PrepareFingerprintEnrollmentAutomaticallyAsync(1, "other")).Succeeded);
        Assert.False((await service.CancelFingerprintEnrollmentPreparationAsync("bad")).Succeeded);
        Assert.False((await service.AuthorizeFingerprintEnrollmentAsync("bad", true)).Succeeded);
        Assert.False((await service.AuthorizeFingerprintEnrollmentAsync(Guid.NewGuid().ToString("D"), true)).Succeeded);
        Assert.False((await service.RevokeFingerprintEnrollmentAuthorizationAsync("bad")).Succeeded);
        Assert.Equal(0, repository.PreparationCalls);
        Assert.True((await service.PrepareFingerprintEnrollmentAsync(1, "Right thumb", 2)).Succeeded);
        Assert.True((await service.PrepareFingerprintEnrollmentAutomaticallyAsync(1, "Right thumb")).Succeeded);
        Assert.True((await service.CancelFingerprintEnrollmentPreparationAsync(Guid.NewGuid().ToString("D"))).Succeeded);
        Assert.True((await service.AuthorizeFingerprintEnrollmentAsync(Guid.NewGuid().ToString("D"), false)).Succeeded);
        Assert.True((await service.RevokeFingerprintEnrollmentAuthorizationAsync(Guid.NewGuid().ToString("D"))).Succeeded);
        Assert.Empty(await service.GetFingerprintEnrollmentPreparationsAsync());
        Assert.Equal(6, repository.PreparationCalls);
    }

    [Fact]
    public async Task PrepareEnrollmentAutomatically_PropagatesRepositoryFailureAndArguments()
    {
        var (service, repository) = CreateService(UserRole.Admin);
        await service.RestoreUserAsync(1);
        repository.AutomaticPreparationResult = OperationResult.Failure("No recent scanner inventory is available.");

        var result = await service.PrepareFingerprintEnrollmentAutomaticallyAsync(42, "Left index");

        Assert.False(result.Succeeded);
        Assert.Equal("No recent scanner inventory is available.", result.Error);
        Assert.Equal(42, repository.AutomaticPreparationTeacherId);
        Assert.Equal("Left index", repository.AutomaticPreparationFinger);
        Assert.Equal(1, repository.PreparationCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.Teacher)]
    public async Task Replacement_RejectsNonAdministrators(UserRole? role)
    {
        var (service, repository) = CreateService(role, true);
        if (role is not null) await service.RestoreUserAsync(1);
        Assert.False((await service.PrepareFingerprintReplacementAsync(1, 2, "Right thumb", 3)).Succeeded);
        Assert.False((await service.AuthorizeFingerprintReplacementAsync(1, 2, "Right thumb", 3, Guid.NewGuid().ToString("D"))).Succeeded);
        Assert.Equal(0, repository.PreparationCalls);
    }

    [Fact]
    public async Task Replacement_IsGatedAndValidatesBeforeDelegating()
    {
        var (disabled, disabledRepository) = CreateService(UserRole.Admin);
        await disabled.RestoreUserAsync(1);
        Assert.False((await disabled.PrepareFingerprintReplacementAsync(1, 2, "Right thumb", 3)).Succeeded);
        Assert.False((await disabled.AuthorizeFingerprintReplacementAsync(1, 2, "Right thumb", 3, Guid.NewGuid().ToString("D"))).Succeeded);
        Assert.Equal(0, disabledRepository.PreparationCalls);

        var (service, repository) = CreateService(UserRole.Admin, true);
        await service.RestoreUserAsync(1);
        Assert.False((await service.PrepareFingerprintReplacementAsync(0, 2, "Right thumb", 3)).Succeeded);
        Assert.False((await service.PrepareFingerprintReplacementAsync(1, 2, "invalid", 3)).Succeeded);
        Assert.False((await service.AuthorizeFingerprintReplacementAsync(1, 2, "Right thumb", 65536, Guid.NewGuid().ToString("D"))).Succeeded);
        Assert.False((await service.AuthorizeFingerprintReplacementAsync(1, 2, "Right thumb", 3, "bad")).Succeeded);
        Assert.Equal(0, repository.PreparationCalls);
        Assert.True((await service.PrepareFingerprintReplacementAsync(1, 2, "Right thumb", 3)).Succeeded);
        Assert.True((await service.AuthorizeFingerprintReplacementAsync(1, 2, "Right thumb", 3, Guid.NewGuid().ToString("D"))).Succeeded);
        Assert.Equal(2, repository.PreparationCalls);
    }

    [Theory]
    [InlineData("EnableSameSlotReplacement")]
    [InlineData("EnableReplacementOperationApi")]
    [InlineData("EnableReplacementGuardReserveApi")]
    [InlineData("EnableReplacementWriteClaimApi")]
    [InlineData("EnableReplacementFinishApi")]
    public void Replacement_RequiresEveryServerFlag(string disabledFlag)
    {
        var values = new Dictionary<string, string?>
        {
            ["EnrollmentSecurity:EnableSameSlotReplacement"] = "true",
            ["EnrollmentSecurity:EnableReplacementOperationApi"] = "true",
            ["EnrollmentSecurity:EnableReplacementGuardReserveApi"] = "true",
            ["EnrollmentSecurity:EnableReplacementWriteClaimApi"] = "true",
            ["EnrollmentSecurity:EnableReplacementFinishApi"] = "true"
        };
        values[$"EnrollmentSecurity:{disabledFlag}"] = "false";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.False(FingerprintReplacementAvailability.IsEnabled(configuration));
        values[$"EnrollmentSecurity:{disabledFlag}"] = "true";
        configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.True(FingerprintReplacementAvailability.IsEnabled(configuration));
    }

    private static (AuthService Service, DeviceRepositoryProxy Repository) CreateService(UserRole? role, bool replacementEnabled = false)
    {
        var accounts = DispatchProxy.Create<IUserAccountRepository, DeviceRepositoryProxy>();
        var proxy = (DeviceRepositoryProxy)accounts;
        proxy.Role = role;
        var service = new AuthService(
            accounts,
            new PasswordHasher(),
            Options.Create(new DefaultAdminOptions()),
            Options.Create(new PasswordResetOptions()),
            null!,
            new LoginAttemptLimiter(),
            Options.Create(new SchoolTimeOptions()),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["EnrollmentSecurity:EnableSameSlotReplacement"] = replacementEnabled.ToString(),
                    ["EnrollmentSecurity:EnableReplacementOperationApi"] = replacementEnabled.ToString(),
                    ["EnrollmentSecurity:EnableReplacementGuardReserveApi"] = replacementEnabled.ToString(),
                    ["EnrollmentSecurity:EnableReplacementWriteClaimApi"] = replacementEnabled.ToString(),
                    ["EnrollmentSecurity:EnableReplacementFinishApi"] = replacementEnabled.ToString()
                }).Build());
        return (service, proxy);
    }
}

public class DeviceRepositoryProxy : DispatchProxy
{
    public UserRole? Role { get; set; }
    public long? DeletedId { get; private set; }
    public OperationResult DeleteResult { get; set; } = OperationResult.Success();
    public long? SelectedScannerId { get; private set; }
    public int SelectionCalls { get; private set; }
    public OperationResult SelectionResult { get; set; } = OperationResult.Success();
    public int SlotBlockCalls { get; private set; }
    public long BlockedSlot { get; private set; }
    public bool BlockedValue { get; private set; }
    public OperationResult SlotBlockResult { get; set; } = OperationResult.Success();
    public int SlotInspectionCalls { get; private set; }
    public int PreparationCalls { get; private set; }
    public long AutomaticPreparationTeacherId { get; private set; }
    public string? AutomaticPreparationFinger { get; private set; }
    public OperationResult AutomaticPreparationResult { get; set; } = OperationResult.Success();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            nameof(IUserAccountRepository.FindByIdAsync) => Task.FromResult<UserAccountRecord?>(
                Role is UserRole role ? new UserAccountRecord(1, "user", "User", null, "", role, false) : null),
            nameof(IUserAccountRepository.DeleteBiometricDeviceAsync) => Delete(args),
            nameof(IUserAccountRepository.SetEnrollmentScannerAsync) => SelectScanner(args),
            nameof(IUserAccountRepository.SetFingerprintSlotBlockedAsync) => SetSlotBlock(args),
            nameof(IUserAccountRepository.GetBlockedFingerprintSlotsAsync) => Task.FromResult<IReadOnlyList<long>>(new long[] { 2 }),
            nameof(IUserAccountRepository.InspectFingerprintSlotAsync) => InspectSlot(args),
            nameof(IUserAccountRepository.PrepareFingerprintEnrollmentAsync) => Prepare(),
            nameof(IUserAccountRepository.PrepareFingerprintEnrollmentAutomaticallyAsync) => PrepareAutomatically(args),
            nameof(IUserAccountRepository.PrepareFingerprintReplacementAsync) => Prepare(),
            nameof(IUserAccountRepository.AuthorizeFingerprintReplacementAsync) => Prepare(),
            nameof(IUserAccountRepository.CancelFingerprintEnrollmentPreparationAsync) => Prepare(),
            nameof(IUserAccountRepository.AuthorizeFingerprintEnrollmentAsync) => Prepare(),
            nameof(IUserAccountRepository.RevokeFingerprintEnrollmentAuthorizationAsync) => Prepare(),
            nameof(IUserAccountRepository.GetFingerprintEnrollmentPreparationsAsync) => Preparations(),
            _ => throw new NotSupportedException($"Unexpected repository call: {targetMethod?.Name}")
        };
    }

    private Task<OperationResult> Prepare()
    {
        PreparationCalls++;
        return Task.FromResult(OperationResult.Success());
    }

    private Task<OperationResult> PrepareAutomatically(object?[]? args)
    {
        PreparationCalls++;
        AutomaticPreparationTeacherId = (long)args![0]!;
        AutomaticPreparationFinger = (string)args[1]!;
        return Task.FromResult(AutomaticPreparationResult);
    }

    private Task<IReadOnlyList<FingerprintEnrollmentPreparation>> Preparations()
    {
        PreparationCalls++;
        return Task.FromResult<IReadOnlyList<FingerprintEnrollmentPreparation>>(Array.Empty<FingerprintEnrollmentPreparation>());
    }

    private Task<OperationResult> Delete(object?[]? args)
    {
        DeletedId = (long)args![0]!;
        return Task.FromResult(DeleteResult);
    }

    private Task<OperationResult> SelectScanner(object?[]? args)
    {
        SelectionCalls++;
        SelectedScannerId = (long?)args![0];
        return Task.FromResult(SelectionResult);
    }

    private Task<OperationResult> SetSlotBlock(object?[]? args)
    {
        SlotBlockCalls++;
        BlockedSlot = (long)args![0]!;
        BlockedValue = (bool)args[1]!;
        return Task.FromResult(SlotBlockResult);
    }

    private Task<FingerprintSlotInspection> InspectSlot(object?[]? args)
    {
        SlotInspectionCalls++;
        return Task.FromResult(new FingerprintSlotInspection((long)args![0]!, true, "Teacher", true));
    }
}
