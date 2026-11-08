using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace SmartClassAC.Services;

internal sealed record AcIrRequest(string? DeviceId, string? Boot, string? Command, string? Challenge, string? Tag);

internal static class AcIrRoutes
{
    internal static void MapAcIrRoutes(this WebApplication app, Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        foreach (var version in new[] { 1, 2 })
        foreach (var action in new[] { "boot", "claim", "complete" })
        {
            var purpose = "ac_ir_" + (version == 2 ? "v2_" : "") + action;
            var route = "/api/device/ac-ir/" + (version == 2 ? "v2/" : "") + action;
            app.MapPost(route + "/challenge",
                async (FacultyOtpChallengeRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
                {
                    var failure = CheckAccess(app, context, configuration, isAuthorizedDevice);
                    if (failure is not null) return failure;
                    if (configuration.GetValue<bool>("AcIr:EnablePowerToggle") != (version == 2))
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId)) return Results.BadRequest();
                    var challenge = await new EnrollmentChallengeStore(configuration).IssueAsync(request.DeviceId!, purpose, cancellationToken);
                    return challenge.Challenge is null ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                        : Results.Json(new { challenge = Convert.ToHexString(challenge.Challenge).ToLowerInvariant() });
                }).WithMetadata(new RequestSizeLimitAttribute(2048));
            app.MapPost(route,
                async (AcIrRequest request, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
                {
                    var failure = CheckAccess(app, context, configuration, isAuthorizedDevice);
                    if (failure is not null) return failure;
                    if (configuration.GetValue<bool>("AcIr:EnablePowerToggle") != (version == 2))
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    var response = await new AcIrProtocol(configuration).ExecuteAsync(purpose, request, cancellationToken);
                    return response is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(response);
                }).WithMetadata(new RequestSizeLimitAttribute(2048));
        }
    }

    private static IResult? CheckAccess(WebApplication app, HttpContext context, IConfiguration configuration,
        Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!configuration.GetValue<bool>("AcIr:EnableDeviceApi")) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!context.Request.IsHttps && !app.Environment.IsDevelopment()) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
        return isAuthorizedDevice(context, configuration) ? null : Results.Unauthorized();
    }
}

internal sealed class AcIrProtocol(IConfiguration configuration)
{
    internal async Task<FacultyOtpResponse?> ExecuteAsync(string purpose, AcIrRequest request,
        CancellationToken cancellationToken = default)
    {
        bool power = purpose.StartsWith("ac_ir_v2_", StringComparison.Ordinal);
        bool boot = purpose == (power ? "ac_ir_v2_boot" : "ac_ir_boot"),
             claim = purpose == (power ? "ac_ir_v2_claim" : "ac_ir_claim"),
             complete = purpose == (power ? "ac_ir_v2_complete" : "ac_ir_complete");
        if ((!boot && !claim && !complete) || !EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !FacultyOtpProtocol.TryHex(request.Boot, out var bootBytes) ||
            (complete ? !TryCommand(request.Command, out _) : request.Command != "-") ||
            !FacultyOtpProtocol.TryHex(request.Challenge, out var challenge) ||
            !FacultyOtpProtocol.TryHex(request.Tag, out var tag)) return null;

        var master = FacultyOtpProtocol.ReadKey(configuration["EnrollmentSecurity:MasterKey"], "EnrollmentSecurity:MasterKey");
        byte[]? key = null;
        try
        {
            key = await new EnrollmentDeviceCredentialStore(configuration).LoadAsync(request.DeviceId!, master, cancellationToken);
            if (key is null) return null;
            var body = Encoding.ASCII.GetBytes(request.Boot + "\n" + request.Command + "\n");
            if (!await new EnrollmentChallengeStore(configuration).VerifyAndConsumeAsync(
                request.DeviceId!, purpose, key, challenge, body, tag, cancellationToken)) return null;

            var store = new AcIrStateStore(configuration);
            string content;
            if (boot)
                content = await store.RegisterBootAsync(request.DeviceId!, bootBytes, cancellationToken, power) ? "registered\n" : "denied\n";
            else if (complete)
            {
                TryCommand(request.Command, out var command);
                content = await store.CompleteAsync(request.DeviceId!, bootBytes, command, cancellationToken, power) ? "completed\n" : "denied\n";
            }
            else
            {
                var step = await store.ClaimAsync(request.DeviceId!, bootBytes, cancellationToken, power);
                if (step is not null)
                    content = FormattableString.Invariant($"{(step.IsPower ? "power" : "step")}\n{Convert.ToHexString(step.Id).ToLowerInvariant()}\n{step.From}\n{step.To}\n");
                else
                {
                    var state = await store.GetAsync(request.DeviceId!, bootBytes, cancellationToken);
                    if (state?.ProtocolVersion != (power ? 2 : 1)) state = null;
                    content = state?.Status switch
                    {
                        AcIrStatus.Reconcile => "reconcile\n",
                        AcIrStatus.Ready when power && state.AssumedPower is null => "reconcile\n",
                        AcIrStatus.Ready => "ready\n",
                        AcIrStatus.InFlight => "inflight\n",
                        _ => "denied\n"
                    };
                }
            }
            var responseBody = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant() + "\n" + content;
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, purpose, challenge,
                Encoding.ASCII.GetBytes(responseBody));
            return new FacultyOtpResponse(responseBody, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool TryCommand(string? value, out byte[] command)
    {
        command = Array.Empty<byte>();
        if (value is not { Length: 32 } || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        command = Convert.FromHexString(value);
        return true;
    }
}
