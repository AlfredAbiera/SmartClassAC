using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartClassAC.Services;
using Xunit;

// Covers the claim routes' gates and input checks. Each rejection happens before any
// database access, so this test does not need MariaDB.
public sealed class EnrollmentClaimRouteTests
{
    [Fact]
    public async Task ClaimRoutes_AreDisabledByDefaultAndRejectBeforeStorageWork()
    {
        var webBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        webBuilder.Logging.ClearProviders();
        webBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = webBuilder.Build();
        app.MapEnrollmentRecoveryRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        var address = Assert.Single(addresses.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        var challengeRequest = new EnrollmentRecoveryChallengeRequest("1000000001");
        var operationRequest = new ReplacementOperationRequest("1000000001", new string('0', 64), new string('0', 64), new string('0', 64));
        using var disabledReplacementOperation = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation", operationRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledReplacementOperation.StatusCode);
        Assert.True(disabledReplacementOperation.Headers.CacheControl?.NoStore);
        using var disabledReplacementOperationChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledReplacementOperationChallenge.StatusCode);
        var guardStatusRequest = new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), 11, new string('0', 64), new string('0', 64), new string('0', 64));
        using var disabledFence = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence", guardStatusRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledFence.StatusCode);
        Assert.True(disabledFence.Headers.CacheControl?.NoStore);
        using var disabledFenceChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledFenceChallenge.StatusCode);
        Assert.True(disabledFenceChallenge.Headers.CacheControl?.NoStore);
        using var disabledWriteClaim = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-claim", guardStatusRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledWriteClaim.StatusCode);
        Assert.True(disabledWriteClaim.Headers.CacheControl?.NoStore);
        using var disabledWriteChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-claim/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledWriteChallenge.StatusCode);
        Assert.True(disabledWriteChallenge.Headers.CacheControl?.NoStore);
        using var disabledGuardReserve = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve", guardStatusRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledGuardReserve.StatusCode);
        Assert.True(disabledGuardReserve.Headers.CacheControl?.NoStore);
        using var disabledGuardReserveChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledGuardReserveChallenge.StatusCode);
        Assert.True(disabledGuardReserveChallenge.Headers.CacheControl?.NoStore);
        using var disabledGuardStatus = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status", guardStatusRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledGuardStatus.StatusCode);
        Assert.True(disabledGuardStatus.Headers.CacheControl?.NoStore);
        using var disabledGuardChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledGuardChallenge.StatusCode);
        Assert.True(disabledGuardChallenge.Headers.CacheControl?.NoStore);
        var replacementFinishRequest = new ReplacementFinishRequest("1000000001", Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), 11, new string('0', 64), new string('0', 64), new string('0', 64));
        using var disabledReplacementFinish = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish", replacementFinishRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledReplacementFinish.StatusCode);
        Assert.True(disabledReplacementFinish.Headers.CacheControl?.NoStore);
        using var disabledReplacementFinishChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledReplacementFinishChallenge.StatusCode);
        Assert.True(disabledReplacementFinishChallenge.Headers.CacheControl?.NoStore);
        var claimRequest = new EnrollmentClaimRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            11, false, new string('0', 64), new string('0', 64));

        using var disabledChallenge = await client.PostAsJsonAsync("api/device/enrollment/claim/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledChallenge.StatusCode);
        Assert.True(disabledChallenge.Headers.CacheControl?.NoStore);
        using var disabledClaim = await client.PostAsJsonAsync("api/device/enrollment/claim", claimRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledClaim.StatusCode);
        using var disabledOperation = await client.PostAsJsonAsync(
            "api/device/enrollment/operation", new EnrollmentOperationRequest("1000000001"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledOperation.StatusCode);
        Assert.True(disabledOperation.Headers.CacheControl?.NoStore);
        using var disabledInventoryChallenge = await client.PostAsJsonAsync("api/device/enrollment/inventory/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledInventoryChallenge.StatusCode);
        using var disabledReplacementChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-intent/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledReplacementChallenge.StatusCode);
        using var disabledReplacement = await client.PostAsJsonAsync("api/device/enrollment/replacement-intent",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 11,
                new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledReplacement.StatusCode);
        Assert.True(disabledReplacement.Headers.CacheControl?.NoStore);
        var statusRequest = new ReplacementAttemptStatusRequest("1000000001", Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 11,
            new string('0', 64), new string('0', 64), new string('0', 64));
        var fenceAttemptRequest = new ReplacementAttemptReserveRequest(statusRequest.DeviceId, statusRequest.PreparationId,
            statusRequest.OperationId, statusRequest.RecoveryOperationId, statusRequest.Slot, statusRequest.Challenge,
            statusRequest.ClientNonce, statusRequest.Tag);
        using var disabledAttemptFence = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence", fenceAttemptRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledAttemptFence.StatusCode);
        Assert.True(disabledAttemptFence.Headers.CacheControl?.NoStore);
        using var disabledAttemptFenceChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledAttemptFenceChallenge.StatusCode);
        Assert.True(disabledAttemptFenceChallenge.Headers.CacheControl?.NoStore);
        using var disabledStatus = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-status", statusRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledStatus.StatusCode);
        Assert.True(disabledStatus.Headers.CacheControl?.NoStore);
        using var disabledStatusChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-status/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledStatusChallenge.StatusCode);
        var reserveRequest = new ReplacementAttemptReserveRequest(statusRequest.DeviceId, statusRequest.PreparationId,
            statusRequest.OperationId, statusRequest.RecoveryOperationId, statusRequest.Slot,
            statusRequest.Challenge, statusRequest.ClientNonce, statusRequest.Tag);
        using var disabledAttemptFinish = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish", reserveRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledAttemptFinish.StatusCode);
        Assert.True(disabledAttemptFinish.Headers.CacheControl?.NoStore);
        using var disabledAttemptFinishChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledAttemptFinishChallenge.StatusCode);
        using var disabledAttemptClaim = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim", reserveRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledAttemptClaim.StatusCode);
        Assert.True(disabledAttemptClaim.Headers.CacheControl?.NoStore);
        using var disabledAttemptClaimChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledAttemptClaimChallenge.StatusCode);
        Assert.True(disabledAttemptClaimChallenge.Headers.CacheControl?.NoStore);
        using var disabledReserve = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-reserve", reserveRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledReserve.StatusCode);
        Assert.True(disabledReserve.Headers.CacheControl?.NoStore);
        using var disabledReserveChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-reserve/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledReserveChallenge.StatusCode);
        Assert.True(disabledReserveChallenge.Headers.CacheControl?.NoStore);
        using var disabledInventory = await client.PostAsJsonAsync("api/device/enrollment/inventory",
            new FingerprintInventoryRequest("1000000001", 8, "00", new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledInventory.StatusCode);
        using var disabledFinishChallenge = await client.PostAsJsonAsync(
            "api/device/enrollment/finish/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledFinishChallenge.StatusCode);
        using var disabledFinish = await client.PostAsJsonAsync("api/device/enrollment/finish",
            new EnrollmentFinishRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledFinish.StatusCode);

        app.Configuration["EnrollmentSecurity:EnableClaimApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableOperationApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableFinishApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableRecoveryApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableInventoryApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementIntentApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptStatusApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptReserveApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementOperationApi"] = "true";
        using var unauthorizedReplacementOperation = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation", operationRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReplacementOperation.StatusCode);
        using var unauthorizedReplacementOperationChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReplacementOperationChallenge.StatusCode);
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptClaimApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptFinishApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptFenceApi"] = "true";
        using var unauthorizedAttemptFence = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence", fenceAttemptRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAttemptFence.StatusCode);
        using var unauthorizedAttemptFenceChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAttemptFenceChallenge.StatusCode);
        using var unauthorizedAttemptFinish = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish", reserveRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAttemptFinish.StatusCode);
        using var unauthorizedAttemptFinishChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAttemptFinishChallenge.StatusCode);
        using var unauthorizedAttemptClaim = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim", reserveRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAttemptClaim.StatusCode);
        using var unauthorizedAttemptChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAttemptChallenge.StatusCode);
        app.Configuration["EnrollmentSecurity:EnableReplacementFinishApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementGuardStatusApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementGuardReserveApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementWriteClaimApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementWriteFenceApi"] = "true";
        using var unauthorizedFence = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence", guardStatusRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedFence.StatusCode);
        using var unauthorizedFenceChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedFenceChallenge.StatusCode);
        using var unauthorizedWriteClaim = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-claim", guardStatusRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedWriteClaim.StatusCode);
        using var unauthorizedWriteChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-claim/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedWriteChallenge.StatusCode);
        using var unauthorizedGuardReserve = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve", guardStatusRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedGuardReserve.StatusCode);
        using var unauthorizedGuardReserveChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedGuardReserveChallenge.StatusCode);
        using var unauthorizedGuardStatus = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status", guardStatusRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedGuardStatus.StatusCode);
        using var unauthorizedGuardChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedGuardChallenge.StatusCode);
        using var unauthorizedReplacementFinish = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish", replacementFinishRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReplacementFinish.StatusCode);
        using var unauthorizedReplacementFinishChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReplacementFinishChallenge.StatusCode);
        using var unauthorizedReserve = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-reserve", reserveRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReserve.StatusCode);
        using var unauthorizedReserveChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-reserve/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReserveChallenge.StatusCode);
        using var unauthorizedStatus = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-status", statusRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedStatus.StatusCode);
        using var unauthorizedStatusChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-status/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedStatusChallenge.StatusCode);
        using var unauthorizedReplacement = await client.PostAsJsonAsync("api/device/enrollment/replacement-intent",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 11,
                new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReplacement.StatusCode);
        using var unauthorizedReplacementChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-intent/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReplacementChallenge.StatusCode);
        using var unauthorizedInventory = await client.PostAsJsonAsync("api/device/enrollment/inventory",
            new FingerprintInventoryRequest("1000000001", 8, "00", new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedInventory.StatusCode);
        using var developmentHttpChallenge = await client.PostAsJsonAsync(
            "api/device/enrollment/claim/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, developmentHttpChallenge.StatusCode);
        using var developmentHttpRecoveryChallenge = await client.PostAsJsonAsync(
            "api/device/enrollment/recovery/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, developmentHttpRecoveryChallenge.StatusCode);
        using var developmentHttpOperation = await client.PostAsJsonAsync(
            "api/device/enrollment/operation", new EnrollmentOperationRequest("1000000001"));
        Assert.Equal(HttpStatusCode.Unauthorized, developmentHttpOperation.StatusCode);
        using var developmentHttpClaim = await client.PostAsJsonAsync("api/device/enrollment/claim", claimRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, developmentHttpClaim.StatusCode);
        using var developmentHttpFinishChallenge = await client.PostAsJsonAsync(
            "api/device/enrollment/finish/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, developmentHttpFinishChallenge.StatusCode);
        using var developmentHttpFinish = await client.PostAsJsonAsync("api/device/enrollment/finish",
            new EnrollmentFinishRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.Unauthorized, developmentHttpFinish.StatusCode);
        using var developmentHttpRecovery = await client.PostAsJsonAsync("api/device/enrollment/recovery",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.Unauthorized, developmentHttpRecovery.StatusCode);

        using var unauthorizedChallenge = await client.PostAsJsonAsync("api/device/enrollment/claim/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedChallenge.StatusCode);
        using var unauthorizedClaim = await client.PostAsJsonAsync("api/device/enrollment/claim", claimRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedClaim.StatusCode);
        using var unauthorizedOperation = await client.PostAsJsonAsync(
            "api/device/enrollment/operation", new EnrollmentOperationRequest("1000000001"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedOperation.StatusCode);
        using var unauthorizedFinishChallenge = await client.PostAsJsonAsync(
            "api/device/enrollment/finish/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedFinishChallenge.StatusCode);
        using var unauthorizedFinish = await client.PostAsJsonAsync("api/device/enrollment/finish",
            new EnrollmentFinishRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedFinish.StatusCode);

        client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
        using var malformedReplacementOperation = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation", operationRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedReplacementOperation.StatusCode);
        Assert.True(malformedReplacementOperation.Headers.CacheControl?.NoStore);
        using var malformedFence = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence", guardStatusRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedFence.StatusCode);
        Assert.True(malformedFence.Headers.CacheControl?.NoStore);
        using var invalidFenceChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence/challenge",
            new EnrollmentRecoveryChallengeRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidFenceChallenge.StatusCode);
        using var malformedWriteClaim = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-claim", guardStatusRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedWriteClaim.StatusCode);
        Assert.True(malformedWriteClaim.Headers.CacheControl?.NoStore);
        using var invalidWriteChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-claim/challenge",
            new EnrollmentRecoveryChallengeRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidWriteChallenge.StatusCode);
        using var malformedGuardReserve = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve", guardStatusRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedGuardReserve.StatusCode);
        Assert.True(malformedGuardReserve.Headers.CacheControl?.NoStore);
        using var invalidGuardReserveChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve/challenge",
            new EnrollmentRecoveryChallengeRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidGuardReserveChallenge.StatusCode);
        using var malformedGuardStatus = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status", guardStatusRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedGuardStatus.StatusCode);
        Assert.True(malformedGuardStatus.Headers.CacheControl?.NoStore);
        using var malformedReplacementFinish = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish", replacementFinishRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedReplacementFinish.StatusCode);
        Assert.True(malformedReplacementFinish.Headers.CacheControl?.NoStore);
        using var invalidReplacementFinishChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish/challenge",
            new EnrollmentRecoveryChallengeRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidReplacementFinishChallenge.StatusCode);
        using var malformedReserve = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-reserve", reserveRequest with { Tag = "bad" });
        using var malformedAttemptClaim = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim", reserveRequest with { Tag = "bad" });
        using var malformedAttemptFinish = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish", reserveRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedAttemptFinish.StatusCode);
        Assert.True(malformedAttemptFinish.Headers.CacheControl?.NoStore);
        using var malformedAttemptFence = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence", fenceAttemptRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedAttemptFence.StatusCode);
        Assert.True(malformedAttemptFence.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Forbidden, malformedAttemptClaim.StatusCode);
        Assert.True(malformedAttemptClaim.Headers.CacheControl?.NoStore);
        using var invalidAttemptChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim/challenge",
            new EnrollmentRecoveryChallengeRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidAttemptChallenge.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, malformedReserve.StatusCode);
        Assert.True(malformedReserve.Headers.CacheControl?.NoStore);
        using var invalidReserveChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-reserve/challenge",
            new EnrollmentRecoveryChallengeRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidReserveChallenge.StatusCode);
        using var malformedStatus = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-status", statusRequest with { Tag = "bad" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedStatus.StatusCode);
        using var malformedReplacement = await client.PostAsJsonAsync("api/device/enrollment/replacement-intent",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 11,
                new string('0', 64), new string('0', 64), "bad"));
        Assert.Equal(HttpStatusCode.Forbidden, malformedReplacement.StatusCode);
        using var invalidOperation = await client.PostAsJsonAsync(
            "api/device/enrollment/operation", new EnrollmentOperationRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidOperation.StatusCode);
        using var invalidFinishChallenge = await client.PostAsJsonAsync(
            "api/device/enrollment/finish/challenge", new EnrollmentRecoveryChallengeRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidFinishChallenge.StatusCode);
        using var malformedFinish = await client.PostAsJsonAsync("api/device/enrollment/finish",
            new EnrollmentFinishRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), "not-hex"));
        Assert.Equal(HttpStatusCode.Forbidden, malformedFinish.StatusCode);
        using var invalidChallenge = await client.PostAsJsonAsync("api/device/enrollment/claim/challenge", new EnrollmentRecoveryChallengeRequest("bad"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidChallenge.StatusCode);
        using var malformedClaim = await client.PostAsJsonAsync("api/device/enrollment/claim", claimRequest with { Tag = "not-hex" });
        Assert.Equal(HttpStatusCode.Forbidden, malformedClaim.StatusCode);
        Assert.True(malformedClaim.Headers.CacheControl?.NoStore);
        using var zeroSlotClaim = await client.PostAsJsonAsync("api/device/enrollment/claim", claimRequest with { Slot = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, zeroSlotClaim.StatusCode);

        using var oversizedContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedClaim = await client.PostAsync("api/device/enrollment/claim", oversizedContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedClaim.StatusCode);
        using var oversizedReserveContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedReserve = await client.PostAsync("api/device/enrollment/replacement-attempt-reserve", oversizedReserveContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedReserve.StatusCode);
        using var oversizedAttemptContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedAttempt = await client.PostAsync("api/device/enrollment/replacement-attempt-claim", oversizedAttemptContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedAttempt.StatusCode);
        using var oversizedAttemptFinishContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedAttemptFinish = await client.PostAsync("api/device/enrollment/replacement-attempt-finish", oversizedAttemptFinishContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedAttemptFinish.StatusCode);
        using var oversizedAttemptFenceContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedAttemptFence = await client.PostAsync("api/device/enrollment/replacement-attempt-fence", oversizedAttemptFenceContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedAttemptFence.StatusCode);
        using var oversizedOperationContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedReplacementOperation = await client.PostAsync("api/device/enrollment/replacement-operation", oversizedOperationContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedReplacementOperation.StatusCode);
        using var oversizedReplacementFinishContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedReplacementFinish = await client.PostAsync("api/device/enrollment/replacement-finish", oversizedReplacementFinishContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedReplacementFinish.StatusCode);
        using var oversizedGuardContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedGuard = await client.PostAsync("api/device/enrollment/replacement-guard-status", oversizedGuardContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedGuard.StatusCode);
        using var oversizedGuardReserveContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedGuardReserve = await client.PostAsync("api/device/enrollment/replacement-guard-reserve", oversizedGuardReserveContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedGuardReserve.StatusCode);
        using var oversizedWriteContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedWrite = await client.PostAsync("api/device/enrollment/replacement-write-claim", oversizedWriteContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedWrite.StatusCode);
        using var oversizedFenceContent = new StringContent("{\"deviceId\":\"" + new string('1', 3000) + "\"}", Encoding.UTF8, "application/json");
        using var oversizedFence = await client.PostAsync("api/device/enrollment/replacement-write-fence", oversizedFenceContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedFence.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task EnrollmentRoutes_RequireHttpsOutsideDevelopment()
    {
        var webBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        webBuilder.Logging.ClearProviders();
        webBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = webBuilder.Build();
        app.MapEnrollmentRecoveryRoutes((_, _) => true);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
        app.Configuration["EnrollmentSecurity:EnableOperationApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableClaimApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableFinishApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableRecoveryApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementIntentApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptStatusApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptReserveApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementOperationApi"] = "true";
        using var replacementOperationChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, replacementOperationChallenge.StatusCode);
        using var replacementOperation = await client.PostAsJsonAsync("api/device/enrollment/replacement-operation",
            new ReplacementOperationRequest("1000000001", new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, replacementOperation.StatusCode);
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptClaimApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptFinishApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementAttemptFenceApi"] = "true";
        using var attemptFenceChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, attemptFenceChallenge.StatusCode);
        using var attemptFence = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-fence",
            new ReplacementAttemptReserveRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), 3, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, attemptFence.StatusCode);
        using var attemptFinishChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, attemptFinishChallenge.StatusCode);
        using var attemptFinish = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-finish",
            new ReplacementAttemptReserveRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), 11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, attemptFinish.StatusCode);
        using var attemptClaimChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, attemptClaimChallenge.StatusCode);
        using var attemptClaim = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-claim",
            new ReplacementAttemptReserveRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), 11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, attemptClaim.StatusCode);
        app.Configuration["EnrollmentSecurity:EnableReplacementFinishApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementGuardStatusApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementGuardReserveApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementWriteClaimApi"] = "true";
        app.Configuration["EnrollmentSecurity:EnableReplacementWriteFenceApi"] = "true";
        using var fenceChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, fenceChallenge.StatusCode);
        using var fence = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-fence",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, fence.StatusCode);
        using var writeChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-claim/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, writeChallenge.StatusCode);
        using var writeClaim = await client.PostAsJsonAsync("api/device/enrollment/replacement-write-claim",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, writeClaim.StatusCode);
        using var guardReserveChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, guardReserveChallenge.StatusCode);
        using var guardReserve = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-reserve",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, guardReserve.StatusCode);
        using var guardChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, guardChallenge.StatusCode);
        using var guardStatus = await client.PostAsJsonAsync("api/device/enrollment/replacement-guard-status",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, guardStatus.StatusCode);
        using var replacementFinishChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, replacementFinishChallenge.StatusCode);
        using var replacementFinish = await client.PostAsJsonAsync("api/device/enrollment/replacement-finish",
            new ReplacementFinishRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, replacementFinish.StatusCode);
        using var reserveChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-reserve/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, reserveChallenge.StatusCode);
        using var reserve = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-reserve",
            new ReplacementAttemptReserveRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), 11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, reserve.StatusCode);
        using var statusChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-status/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, statusChallenge.StatusCode);
        using var status = await client.PostAsJsonAsync("api/device/enrollment/replacement-attempt-status",
            new ReplacementAttemptStatusRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), 11, new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, status.StatusCode);
        using var replacementChallenge = await client.PostAsJsonAsync("api/device/enrollment/replacement-intent/challenge",
            new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, replacementChallenge.StatusCode);
        using var replacement = await client.PostAsJsonAsync("api/device/enrollment/replacement-intent",
            new EnrollmentRecoveryRequest("1000000001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 11,
                new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, replacement.StatusCode);

        app.Configuration["EnrollmentSecurity:EnableInventoryApi"] = "true";
        using var inventoryChallenge = await client.PostAsJsonAsync(
            "api/device/enrollment/inventory/challenge", new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, inventoryChallenge.StatusCode);
        using var inventory = await client.PostAsJsonAsync("api/device/enrollment/inventory",
            new FingerprintInventoryRequest("1000000001", 8, "00", new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, inventory.StatusCode);

        using var operation = await client.PostAsJsonAsync(
            "api/device/enrollment/operation", new EnrollmentOperationRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, operation.StatusCode);
        using var challenge = await client.PostAsJsonAsync(
            "api/device/enrollment/claim/challenge", new EnrollmentRecoveryChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, challenge.StatusCode);
        using var recovery = await client.PostAsJsonAsync(
            "api/device/enrollment/recovery", new EnrollmentRecoveryRequest("1000000001",
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 11,
                new string('0', 64), new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, recovery.StatusCode);
        using var claim = await client.PostAsJsonAsync(
            "api/device/enrollment/claim", new EnrollmentClaimRequest("1000000001",
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 11, false,
                new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, claim.StatusCode);
        using var finish = await client.PostAsJsonAsync(
            "api/device/enrollment/finish", new EnrollmentFinishRequest("1000000001",
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 11,
                new string('0', 64), new string('0', 64)));
        Assert.Equal(HttpStatusCode.UpgradeRequired, finish.StatusCode);
        await app.StopAsync();
    }
}
