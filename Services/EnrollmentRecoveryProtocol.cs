using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace SmartClassAC.Services;

internal sealed record EnrollmentRecoveryChallengeRequest(string? DeviceId);
internal sealed record ReplacementOperationRequest(string? DeviceId, string? Challenge, string? ClientNonce, string? Tag);
internal sealed record EnrollmentRecoveryRequest(
    string? DeviceId, string? PreparationId, string? OperationId, ushort Slot,
    string? Challenge, string? ClientNonce, string? Tag);

internal sealed record EnrollmentRecoveryResponse(string Body, string Tag);

internal sealed record ReplacementAttemptStatusRequest(
    string? DeviceId, string? PreparationId, string? OperationId, string? RecoveryOperationId,
    ushort Slot, string? Challenge, string? ClientNonce, string? Tag);

internal sealed record ReplacementAttemptReserveRequest(
    string? DeviceId, string? PreparationId, string? OperationId, string? RecoveryOperationId,
    ushort Slot, string? Challenge, string? ClientNonce, string? Tag);

internal sealed record EnrollmentClaimRequest(
    string? DeviceId, string? PreparationId, string? OperationId, ushort Slot, bool Occupied,
    string? Challenge, string? Tag);

internal sealed record EnrollmentFinishRequest(
    string? DeviceId, string? PreparationId, string? OperationId, ushort Slot,
    string? Challenge, string? Tag);

internal sealed record ReplacementFinishRequest(
    string? DeviceId, string? PreparationId, string? OperationId, ushort Slot,
    string? Challenge, string? ClientNonce, string? Tag);

internal sealed record EnrollmentOperationRequest(string? DeviceId);

internal static class EnrollmentRecoveryRoutes
{
    public static void MapEnrollmentRecoveryRoutes(this WebApplication app, Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        app.MapPost("/api/device/enrollment/operation", async (
            EnrollmentOperationRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableOperationApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId))
                return Results.BadRequest();

            var result = await new FingerprintWriteGuardStore(configuration)
                .GetAuthorizedOperationAsync(request.DeviceId!, cancellationToken);
            if (result is null)
                return Results.NotFound();
            return Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        MapChallengeRoute(app, "/api/device/enrollment/recovery/challenge", "recover", "EnrollmentSecurity:EnableRecoveryApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/claim/challenge", "claim", "EnrollmentSecurity:EnableClaimApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/finish/challenge", "finish", "EnrollmentSecurity:EnableFinishApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/inventory/challenge", "inventory", "EnrollmentSecurity:EnableInventoryApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-intent/challenge", "replacement_intent", "EnrollmentSecurity:EnableReplacementIntentApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-attempt-status/challenge", "replacement_attempt_status", "EnrollmentSecurity:EnableReplacementAttemptStatusApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-attempt-reserve/challenge", "replacement_attempt_reserve", "EnrollmentSecurity:EnableReplacementAttemptReserveApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-finish/challenge", "replacement_finish", "EnrollmentSecurity:EnableReplacementFinishApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-guard-status/challenge", "replacement_guard_status", "EnrollmentSecurity:EnableReplacementGuardStatusApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-guard-reserve/challenge", "replacement_guard_reserve", "EnrollmentSecurity:EnableReplacementGuardReserveApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-write-claim/challenge", "replacement_write_claim", "EnrollmentSecurity:EnableReplacementWriteClaimApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-write-fence/challenge", "replacement_write_fence", "EnrollmentSecurity:EnableReplacementWriteFenceApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-attempt-claim/challenge", "replacement_attempt_claim", "EnrollmentSecurity:EnableReplacementAttemptClaimApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-attempt-finish/challenge", "replacement_attempt_finish", "EnrollmentSecurity:EnableReplacementAttemptFinishApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-attempt-fence/challenge", "replacement_attempt_fence", "EnrollmentSecurity:EnableReplacementAttemptFenceApi", isAuthorizedDevice);
        MapChallengeRoute(app, "/api/device/enrollment/replacement-operation/challenge", "replacement_operation", "EnrollmentSecurity:EnableReplacementOperationApi", isAuthorizedDevice);

        app.MapPost("/api/device/enrollment/replacement-attempt-fence", async (
            ReplacementAttemptReserveRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementAttemptFenceApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).FenceReplacementAttemptAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-operation", async (
            ReplacementOperationRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementOperationApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).GetReplacementOperationAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-attempt-finish", async (
            ReplacementAttemptReserveRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementAttemptFinishApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).FinishReplacementAttemptAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-attempt-claim", async (
            ReplacementAttemptReserveRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementAttemptClaimApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).ClaimReplacementAttemptAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-write-fence", async (
            EnrollmentRecoveryRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementWriteFenceApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).FenceReplacementWriteAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-write-claim", async (
            EnrollmentRecoveryRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementWriteClaimApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).ClaimReplacementWriteAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-guard-reserve", async (
            EnrollmentRecoveryRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementGuardReserveApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).ReserveReplacementGuardAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-guard-status", async (
            EnrollmentRecoveryRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementGuardStatusApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).GetReplacementGuardStatusAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-finish", async (
            ReplacementFinishRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementFinishApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).FinishReplacementAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-attempt-reserve", async (
            ReplacementAttemptReserveRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementAttemptReserveApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).ReserveReplacementAttemptAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-attempt-status", async (
            ReplacementAttemptStatusRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementAttemptStatusApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).GetReplacementAttemptStatusAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/replacement-intent", async (
            EnrollmentRecoveryRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableReplacementIntentApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).GetReplacementIntentAsync(request, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/inventory", async (
            FingerprintInventoryRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableInventoryApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            if (!await new FingerprintSensorInventory(configuration).RecordAsync(request, cancellationToken))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok();
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/recovery", async (
            EnrollmentRecoveryRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableRecoveryApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).RecoverAsync(request, cancellationToken);
            if (result is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/claim", async (
            EnrollmentClaimRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableClaimApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).ClaimAsync(request, cancellationToken);
            if (result is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/enrollment/finish", async (
            EnrollmentFinishRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>("EnrollmentSecurity:EnableFinishApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            var result = await new EnrollmentRecoveryProtocol(configuration).FinishAsync(request, cancellationToken);
            if (result is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Json(result);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));
    }

    private static void MapChallengeRoute(WebApplication app, string route, string operation, string flag,
        Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        app.MapPost(route, async (
            EnrollmentRecoveryChallengeRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!configuration.GetValue<bool>(flag))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!isAuthorizedDevice(context, configuration))
                return Results.Unauthorized();
            if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId))
                return Results.BadRequest();

            var result = await new EnrollmentChallengeStore(configuration).IssueAsync(request.DeviceId!, operation, cancellationToken);
            if (result.Challenge is null)
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            return Results.Json(new { challenge = Convert.ToHexString(result.Challenge).ToLowerInvariant() });
        }).WithMetadata(new RequestSizeLimitAttribute(2048));
    }
}

/// <summary>
/// Authenticates recovery requests and signs the exact recovery state and operation.
/// A response is not a write authorization.
/// </summary>
internal sealed class EnrollmentRecoveryProtocol
{
    private readonly IConfiguration _configuration;
    private readonly FingerprintWriteGuardStore _guards;

    public EnrollmentRecoveryProtocol(IConfiguration configuration)
    {
        _configuration = configuration;
        _guards = new FingerprintWriteGuardStore(configuration);
    }

    public async Task<EnrollmentRecoveryResponse?> RecoverAsync(EnrollmentRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out var clientNonce) ||
            !TryDecodeHex(request.Tag, out var tag))
            return null;

        var result = await _guards.GetAuthenticatedRecoveryStateAsync(
            preparation.ToString("D"), request.DeviceId!, operation.ToString("D"), request.Slot, challenge, clientNonce, tag, cancellationToken);
        if (result.State is not { } state)
            return null;

        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null)
            return null;
        try
        {
            var stateText = state switch
            {
                EnrollmentRecoveryState.Unresolved => "unresolved",
                EnrollmentRecoveryState.Completed => "completed",
                _ => throw new InvalidOperationException("Unknown enrollment recovery state.")
            };
            var body = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{request.Slot}\n{request.ClientNonce}\n{stateText}\n");
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, "recover", challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // The scanner attests a durable local fence, not sensor contents.
    public async Task<EnrollmentRecoveryResponse?> FenceReplacementWriteAsync(EnrollmentRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;
        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_write_fence";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{request.Slot}\n{request.ClientNonce}\nfenced\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var result = await _guards.FenceReplacementWriteAsync(request.DeviceId!, preparation, operation, request.Slot,
                SHA256.HashData(challenge), cancellationToken);
            if (!result.Succeeded) return null;
            var body = $"{requestBody}fenced_only\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // A claim is one-use and excludes recovery until a safe transition is implemented.
    public async Task<EnrollmentRecoveryResponse?> ClaimReplacementWriteAsync(EnrollmentRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;
        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_write_claim";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{request.Slot}\n{request.ClientNonce}\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var result = await _guards.ClaimReplacementWriteAsync(request.DeviceId!, preparation, operation, request.Slot,
                SHA256.HashData(challenge), cancellationToken);
            if (!result.Succeeded) return null;
            var body = $"{requestBody}granted\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // A signed reservation receipt is not a sensor-write grant.
    public async Task<EnrollmentRecoveryResponse?> ReserveReplacementGuardAsync(EnrollmentRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;
        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_guard_reserve";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{request.Slot}\n{request.ClientNonce}\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var result = await _guards.ReserveAuthenticatedReplacementGuardAsync(preparation.ToString("D"), request.DeviceId!,
                operation.ToString("D"), request.Slot, SHA256.HashData(challenge), cancellationToken);
            if (!result.Succeeded) return null;
            var body = $"{requestBody}guarded_only\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<EnrollmentRecoveryResponse?> GetReplacementGuardStatusAsync(EnrollmentRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;
        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_guard_status";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{request.Slot}\n{request.ClientNonce}\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var status = await _guards.GetReplacementGuardStatusAsync(request.DeviceId!, preparation, operation,
                request.Slot, SHA256.HashData(challenge), cancellationToken);
            if (status is null) return null;
            var body = $"{requestBody}{status}\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<EnrollmentRecoveryResponse?> GetReplacementIntentAsync(EnrollmentRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;

        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{request.Slot}\n{request.ClientNonce}\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, "replacement_intent", challenge,
                Encoding.ASCII.GetBytes(requestBody), tag))
                return null;
            var recoveryOperation = await new FingerprintReplacementRecoveryIntent(_configuration).FindAuthenticatedAsync(
                request.DeviceId!, preparation, operation, request.Slot, SHA256.HashData(challenge), cancellationToken);
            if (recoveryOperation is null) return null;
            var body = FormattableString.Invariant($"{requestBody}{recoveryOperation}\nintent_only\n");
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, "replacement_intent",
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<EnrollmentRecoveryResponse?> GetReplacementAttemptStatusAsync(ReplacementAttemptStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            !Guid.TryParseExact(request.RecoveryOperationId, "D", out var recovery) || recovery == Guid.Empty ||
            recovery == operation || request.Slot == 0 ||
            !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;

        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_attempt_status";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{recovery:D}\n{request.Slot}\n{request.ClientNonce}\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var state = await new FingerprintReplacementRecoveryIntent(_configuration).FindAttemptStatusAsync(
                request.DeviceId!, preparation, operation, recovery, request.Slot, SHA256.HashData(challenge), cancellationToken);
            if (state is null) return null;
            var body = $"{requestBody}{state}\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // Operation discovery is authenticated but does not reserve a guard or authorize a write.
    public async Task<EnrollmentRecoveryResponse?> GetReplacementOperationAsync(ReplacementOperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !TryDecodeHex(request.Challenge, out var challenge) || !TryDecodeHex(request.ClientNonce, out _) ||
            !TryDecodeHex(request.Tag, out var tag))
            return null;
        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_operation";
            var requestBody = $"{request.ClientNonce}\n";
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var operation = await _guards.GetAuthenticatedReplacementOperationAsync(
                request.DeviceId!, SHA256.HashData(challenge), cancellationToken);
            if (operation is null) return null;
            var body = FormattableString.Invariant($"{operation.PreparationId}\n{operation.OperationId}\n{operation.Slot}\n{request.ClientNonce}\nreplacement\n");
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // The device attests its confirmed recovery journal; the MAC is not proof of sensor contents.
    public async Task<EnrollmentRecoveryResponse?> FinishReplacementAttemptAsync(ReplacementAttemptReserveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            !Guid.TryParseExact(request.RecoveryOperationId, "D", out var recovery) || recovery == Guid.Empty ||
            recovery == operation || request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;
        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_attempt_finish";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{recovery:D}\n{request.Slot}\n{request.ClientNonce}\nconfirmed\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var result = await new FingerprintRecoveryCompletion(_configuration).CompleteAsync(
                request.DeviceId!, preparation, operation, recovery, request.Slot, SHA256.HashData(challenge), cancellationToken);
            if (!result.Succeeded) return null;
            var body = $"{requestBody}completed\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // The scanner attests a durable local attempt fence, not sensor contents.
    public async Task<EnrollmentRecoveryResponse?> FenceReplacementAttemptAsync(ReplacementAttemptReserveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            !Guid.TryParseExact(request.RecoveryOperationId, "D", out var recovery) || recovery == Guid.Empty ||
            recovery == operation || request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;
        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_attempt_fence";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{recovery:D}\n{request.Slot}\n{request.ClientNonce}\nfenced\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var result = await new FingerprintReplacementRecoveryIntent(_configuration).FenceAttemptAsync(
                request.DeviceId!, preparation, operation, recovery, request.Slot, SHA256.HashData(challenge), cancellationToken);
            if (!result.Succeeded) return null;
            var body = $"{requestBody}fenced_only\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // A one-use recovery claim is valid only for a fenced, reserved attempt.
    public async Task<EnrollmentRecoveryResponse?> ClaimReplacementAttemptAsync(ReplacementAttemptReserveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            !Guid.TryParseExact(request.RecoveryOperationId, "D", out var recovery) || recovery == Guid.Empty ||
            recovery == operation || request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;
        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_attempt_claim";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{recovery:D}\n{request.Slot}\n{request.ClientNonce}\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var result = await new FingerprintReplacementRecoveryIntent(_configuration).ClaimAttemptAsync(
                request.DeviceId!, preparation, operation, recovery, request.Slot, SHA256.HashData(challenge), cancellationToken);
            if (!result.Succeeded) return null;
            var body = $"{requestBody}granted\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // Reserves server state only. The signed receipt is not a sensor-write grant.
    public async Task<EnrollmentRecoveryResponse?> ReserveReplacementAttemptAsync(ReplacementAttemptReserveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            !Guid.TryParseExact(request.RecoveryOperationId, "D", out var recovery) || recovery == Guid.Empty ||
            recovery == operation || request.Slot == 0 ||
            !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;

        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_attempt_reserve";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{recovery:D}\n{request.Slot}\n{request.ClientNonce}\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var result = await new FingerprintReplacementRecoveryIntent(_configuration).ReserveAuthenticatedAttemptAsync(
                request.DeviceId!, preparation, operation, recovery, request.Slot, SHA256.HashData(challenge), cancellationToken);
            if (!result.Succeeded) return null;
            var body = $"{requestBody}reserved_only\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// Records a server write guard for an authenticated claim and signs the grant.
    /// The key is loaded before the claim so a configuration failure cannot consume the claim.
    /// </summary>
    public async Task<EnrollmentRecoveryResponse?> ClaimAsync(EnrollmentClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.Tag, out var tag))
            return null;

        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null)
            return null;
        try
        {
            var claim = await _guards.ClaimAuthorizedWriteAsync(
                preparation.ToString("D"), request.DeviceId!, operation.ToString("D"), request.Slot, request.Occupied,
                challenge, tag, cancellationToken);
            if (!claim.Succeeded)
                return null;
            var body = FormattableString.Invariant(
                $"{preparation:D}\n{operation:D}\n{request.Slot}\n{(request.Occupied ? "occupied" : "empty")}\ngranted\n");
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, "claim", challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// Commits a scanner-authenticated confirmation and signs the durable completion receipt.
    /// </summary>
    public async Task<EnrollmentRecoveryResponse?> FinishAsync(EnrollmentFinishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.Tag, out var tag))
            return null;

        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null)
            return null;
        try
        {
            var result = await _guards.FinalizeAuthenticatedWriteAsync(
                preparation.ToString("D"), request.DeviceId!, operation.ToString("D"), request.Slot,
                challenge, tag, cancellationToken);
            if (!result.Succeeded)
                return null;
            var body = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{request.Slot}\ncompleted\n");
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(
                key, true, request.DeviceId!, "finish", challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // The device attests that its confirmed journal is durable; a MAC alone does not prove sensor contents.
    public async Task<EnrollmentRecoveryResponse?> FinishReplacementAsync(ReplacementFinishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !Guid.TryParseExact(request.PreparationId, "D", out var preparation) || preparation == Guid.Empty ||
            !Guid.TryParseExact(request.OperationId, "D", out var operation) || operation == Guid.Empty ||
            request.Slot == 0 || !TryDecodeHex(request.Challenge, out var challenge) ||
            !TryDecodeHex(request.ClientNonce, out _) || !TryDecodeHex(request.Tag, out var tag))
            return null;

        var key = await LoadKeyAsync(request.DeviceId!, cancellationToken);
        if (key is null) return null;
        try
        {
            const string purpose = "replacement_finish";
            var requestBody = FormattableString.Invariant($"{preparation:D}\n{operation:D}\n{request.Slot}\n{request.ClientNonce}\nconfirmed\n");
            if (!EnrollmentMessageAuthentication.VerifyTag(key, false, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(requestBody), tag)) return null;
            var result = await _guards.FinalizeAuthenticatedReplacementAsync(
                preparation.ToString("D"), request.DeviceId!, operation.ToString("D"), request.Slot,
                SHA256.HashData(challenge), cancellationToken);
            if (!result.Succeeded) return null;
            var body = $"{requestBody}completed\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose,
                challenge, Encoding.ASCII.GetBytes(body));
            return new(body, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task<byte[]?> LoadKeyAsync(string deviceId, CancellationToken cancellationToken)
    {
        var configured = _configuration["EnrollmentSecurity:MasterKey"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Set EnrollmentSecurity:MasterKey before using enrollment recovery.");
        byte[] master;
        try { master = Convert.FromBase64String(configured); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("EnrollmentSecurity:MasterKey must be a Base64-encoded 32-byte key.", exception);
        }
        try
        {
            if (master.Length != EnrollmentMessageAuthentication.KeySize)
                throw new InvalidOperationException("EnrollmentSecurity:MasterKey must decode to 32 bytes.");
            return await new EnrollmentDeviceCredentialStore(_configuration).LoadAsync(deviceId, master, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    private static bool TryDecodeHex(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is not { Length: 64 } || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
        bytes = Convert.FromHexString(value);
        return true;
    }
}
