using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class DeviceAdminAssignmentTests
{
    private const string Pin = "123456";

    [Fact]
    public async Task LegacyEnrollment_RejectsRequestsWithoutChangingMappings()
    {
        var (service, repository) = Create();

        var result = await service.EnrollAsync(new EnrollFingerprintRequest("1234", 2, "1000000001"));

        Assert.False(result.Ok);
        Assert.Contains("unavailable", result.Error);
        Assert.Equal(0, repository.EnrollmentCalls);
    }

    [Fact]
    public async Task ListRooms_RejectsWrongPin_AndLocksOutAfterRepeatedFailures()
    {
        var (service, _) = Create();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await service.ListAssignableRoomsAsync("1000000001", "000000");
            Assert.Equal(StatusCodes.Status401Unauthorized, failed.StatusCode);
        }

        var locked = await service.ListAssignableRoomsAsync("1000000001", Pin);
        Assert.Equal(StatusCodes.Status429TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task ListRooms_ReturnsFreeRoomsAndCurrentRoomOnly()
    {
        var (service, _) = Create();

        var result = await service.ListAssignableRoomsAsync("1000000001", Pin);

        Assert.True(result.Ok);
        Assert.Equal(new long[] { 1, 3 }, result.Rooms!.Select(room => room.Id).ToArray());
        Assert.True(result.Rooms![0].IsCurrent);
    }

    [Fact]
    public async Task Assign_RejectsRoomTakenByAnotherDevice()
    {
        var (service, repository) = Create();

        var result = await service.AssignRoomAsync("1000000001", Pin, 2);

        Assert.False(result.Ok);
        Assert.Null(repository.Updated);
    }

    [Fact]
    public async Task Assign_UsesRoomTargetTemperature_AndUnassignClearsIt()
    {
        var (service, repository) = Create();

        var assigned = await service.AssignRoomAsync("1000000001", Pin, 3);
        Assert.True(assigned.Ok);
        Assert.Equal(3, repository.Updated!.ClassroomId);
        Assert.Equal(24, repository.Updated.TargetTemperature);

        var unassigned = await service.AssignRoomAsync("1000000001", Pin, null);
        Assert.True(unassigned.Ok);
        Assert.Null(repository.Updated.ClassroomId);
        Assert.Null(repository.Updated.TargetTemperature);
    }

    [Fact]
    public async Task Assign_RequiresValidPin()
    {
        var (service, repository) = Create();

        var result = await service.AssignRoomAsync("1000000001", "12", 3);

        Assert.False(result.Ok);
        Assert.Null(repository.Updated);
    }

    [Fact]
    public async Task Pair_ReturnsNumericDeviceId_WhenPinAndCodeAreValid()
    {
        var (service, repository) = Create();

        var result = await service.PairAsync(Pin, "654321", "10.0.0.5");

        Assert.True(result.Ok);
        Assert.Equal("1000000001", result.DeviceId);
        Assert.Equal("654321", repository.PairedCode);
    }

    [Theory]
    [InlineData("000000", "654321")]
    [InlineData(Pin, "111111")]
    [InlineData(Pin, "12")]
    public async Task Pair_RejectsBadPinOrCode(string pin, string code)
    {
        var (service, _) = Create();

        var result = await service.PairAsync(pin, code, "10.0.0.5");

        Assert.False(result.Ok);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Pair_LocksOutAfterRepeatedFailures()
    {
        var (service, _) = Create();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await service.PairAsync(Pin, "111111", "10.0.0.5");
        }

        var locked = await service.PairAsync(Pin, "654321", "10.0.0.5");
        Assert.Equal(StatusCodes.Status429TooManyRequests, locked.StatusCode);
    }

    private static (DeviceFingerprintService Service, AdminAssignRepositoryProxy Repository) Create()
    {
        var hasher = new PasswordHasher();
        var accounts = DispatchProxy.Create<IUserAccountRepository, AdminAssignRepositoryProxy>();
        var proxy = (AdminAssignRepositoryProxy)accounts;
        proxy.PinHash = hasher.Hash(Pin);
        var service = new DeviceFingerprintService(
            accounts,
            Options.Create(new SchoolTimeOptions()),
            hasher,
            new LoginAttemptLimiter());
        return (service, proxy);
    }
}

public class AdminAssignRepositoryProxy : DispatchProxy
{
    public string PinHash { get; set; } = string.Empty;
    public UpdateBiometricDeviceRequest? Updated { get; private set; }
    public string? PairedCode { get; private set; }
    public int EnrollmentCalls { get; private set; }

    private static readonly BiometricDeviceEntry Device1 = new(1, 1, "Room 1", "1000000001", "Device 1", true, 22, 30, true, true);
    private static readonly BiometricDeviceEntry Device2 = new(2, 2, "Room 2", "1000000002", "Device 2", true, 22, 30, true, true);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            nameof(IUserAccountRepository.FindBiometricDeviceByIdAsync) => Task.FromResult<BiometricDeviceEntry?>(Device1),
            nameof(IUserAccountRepository.GetAdminDevicePinHashesAsync) => Task.FromResult<IReadOnlyList<string>>(new[] { PinHash }),
            nameof(IUserAccountRepository.GetBiometricDevicesAsync) => Task.FromResult<IReadOnlyList<BiometricDeviceEntry>>(new[] { Device1, Device2 }),
            nameof(IUserAccountRepository.GetClassroomsAsync) => Task.FromResult<IReadOnlyList<ClassroomEntry>>(new[]
            {
                new ClassroomEntry(1, "Room 1", 40, 22, null, "Idle", true),
                new ClassroomEntry(2, "Room 2", 40, 22, null, "Idle", true),
                new ClassroomEntry(3, "Room 3", 40, 24, null, "Idle", true)
            }),
            nameof(IUserAccountRepository.UpdateBiometricDeviceAsync) => Update(args),
            nameof(IUserAccountRepository.PairDeviceByCodeAsync) => Pair(args),
            nameof(IUserAccountRepository.EnrollFingerprintByPinAsync) => Enroll(),
            _ => throw new NotSupportedException($"Unexpected repository call: {targetMethod?.Name}")
        };
    }

    private Task<DeviceFingerprintEnrollResult> Enroll()
    {
        EnrollmentCalls++;
        return Task.FromResult(new DeviceFingerprintEnrollResult(true));
    }

    private Task<BiometricDeviceEntry?> Pair(object?[]? args)
    {
        var code = (string)args![0]!;
        if (code != "654321")
        {
            return Task.FromResult<BiometricDeviceEntry?>(null);
        }

        PairedCode = code;
        return Task.FromResult<BiometricDeviceEntry?>(Device1);
    }

    private Task<OperationResult> Update(object?[]? args)
    {
        Updated = (UpdateBiometricDeviceRequest)args![0]!;
        return Task.FromResult(OperationResult.Success());
    }
}
