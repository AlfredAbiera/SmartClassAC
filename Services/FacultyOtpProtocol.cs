using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace SmartClassAC.Services;

internal sealed record FacultyOtpChallengeRequest(string? DeviceId);
internal sealed record FacultyOtpRequest(string? DeviceId, string? Value, string? Challenge, string? Tag);
internal sealed record FacultyOtpResponse(string Body, string Tag);

internal static class FacultyOtpRoutes
{
    public static void MapFacultyOtpRoutes(this WebApplication app, Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        MapChallenge(app, "/api/device/faculty/otp/request/challenge", "faculty_otp_request", isAuthorizedDevice);
        MapChallenge(app, "/api/device/faculty/otp/verify/challenge", "faculty_otp_verify", isAuthorizedDevice);
        MapOperation(app, "/api/device/faculty/otp/request", "faculty_otp_request", isAuthorizedDevice);
        MapOperation(app, "/api/device/faculty/otp/verify", "faculty_otp_verify", isAuthorizedDevice);
    }

    private static IResult? CheckAccess(WebApplication app, HttpContext context, IConfiguration configuration,
        Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!configuration.GetValue<bool>("FacultyOtp:EnableDeviceApi"))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
            return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
        return isAuthorizedDevice(context, configuration) ? null : Results.Unauthorized();
    }

    private static void MapChallenge(WebApplication app, string path, string operation,
        Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        app.MapPost(path, async (FacultyOtpChallengeRequest request, HttpContext context,
            IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            var failure = CheckAccess(app, context, configuration, isAuthorizedDevice);
            if (failure is not null) return failure;
            if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId)) return Results.BadRequest();
            var result = await new EnrollmentChallengeStore(configuration).IssueAsync(request.DeviceId!, operation, cancellationToken);
            return result.Challenge is null
                ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                : Results.Json(new { challenge = Convert.ToHexString(result.Challenge).ToLowerInvariant() });
        }).WithMetadata(new RequestSizeLimitAttribute(2048));
    }

    private static void MapOperation(WebApplication app, string path, string operation,
        Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        app.MapPost(path, async (FacultyOtpRequest request, HttpContext context, IConfiguration configuration,
            GsmSmsTransport transport, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var failure = CheckAccess(app, context, configuration, isAuthorizedDevice);
            if (failure is not null) return failure;
            var protocol = new FacultyOtpProtocol(configuration, transport, loggerFactory.CreateLogger<FacultyOtpProtocol>());
            var response = await protocol.ExecuteAsync(operation, request, cancellationToken);
            return response is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(response);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));
    }
}

internal sealed class FacultyOtpProtocol(IConfiguration configuration, GsmSmsTransport transport, ILogger<FacultyOtpProtocol> logger)
{
    public async Task<FacultyOtpResponse?> ExecuteAsync(string operation, FacultyOtpRequest request,
        CancellationToken cancellationToken = default)
    {
        var issuing = operation == "faculty_otp_request";
        if ((!issuing && operation != "faculty_otp_verify") ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            !ValidValue(request.Value, issuing) ||
            !TryHex(request.Challenge, out var challenge) || !TryHex(request.Tag, out var tag))
            return null;

        var secret = ReadKey(configuration["FacultyOtp:Secret"], "FacultyOtp:Secret");
        byte[]? master = null;
        byte[]? deviceKey = null;
        try
        {
            master = ReadKey(configuration["EnrollmentSecurity:MasterKey"], "EnrollmentSecurity:MasterKey");
            deviceKey = await new EnrollmentDeviceCredentialStore(configuration).LoadAsync(request.DeviceId!, master, cancellationToken);
            if (deviceKey is null) return null;
            var body = Encoding.ASCII.GetBytes(request.Value + "\n");
            if (!await new EnrollmentChallengeStore(configuration).VerifyAndConsumeAsync(
                request.DeviceId!, operation, deviceKey, challenge, body, tag, cancellationToken))
                return null;
            var store = new FacultyOtpStore(configuration);
            string status;
            if (issuing)
            {
                var result = await new FacultyOtpIssuer(store, new GsmModemSettingsStore(configuration), transport)
                    .IssueAsync(request.DeviceId!, request.Value!, secret, cancellationToken);
                if (result.Result == FacultyOtpIssueResult.DeliveryFailed)
                    logger.LogWarning("Faculty OTP issuance failed: modem result {TransportResult}; reservation updated {ReservationUpdated}.",
                        result.TransportResult, result.ReservationUpdated);
                status = "processed";
            }
            else
            {
                status = await store.VerifyAsync(request.DeviceId!, request.Value!, secret, cancellationToken) ? "accepted" : "denied";
            }
            var requestDigest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var responseBody = requestDigest + "\n" + status + "\n";
            var responseTag = EnrollmentMessageAuthentication.ComputeTag(deviceKey, true, request.DeviceId!, operation,
                challenge, Encoding.ASCII.GetBytes(responseBody));
            return new FacultyOtpResponse(responseBody, Convert.ToHexString(responseTag).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (master is not null) CryptographicOperations.ZeroMemory(master);
            if (deviceKey is not null) CryptographicOperations.ZeroMemory(deviceKey);
        }
    }

    private static bool ValidValue(string? value, bool issuing) =>
        value is not null && value.Length == (issuing ? 11 : 6) &&
        (!issuing || value.StartsWith("09", StringComparison.Ordinal)) && value.All(char.IsAsciiDigit);

    internal static bool TryHex(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is not { Length: 64 } || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        bytes = Convert.FromHexString(value);
        return true;
    }

    internal static byte[] ReadKey(string? value, string name)
    {
        byte[] key;
        try { key = Convert.FromBase64String(value ?? ""); }
        catch (FormatException exception) { throw new InvalidOperationException($"{name} must be a Base64-encoded 32-byte key.", exception); }
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidOperationException($"{name} must be a Base64-encoded 32-byte key.");
    }
}
