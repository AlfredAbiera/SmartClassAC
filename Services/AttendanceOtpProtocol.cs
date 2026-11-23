using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace SmartClassAC.Services;

internal static class AttendanceOtpRoutes
{
    private const string Operation = "attendance_otp_verify";

    public static void MapAttendanceOtpRoutes(this WebApplication app, Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        app.MapPost("/api/device/attendance/otp/challenge", async (FacultyOtpChallengeRequest request,
            HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            var failure = CheckAccess(app, context, configuration, isAuthorizedDevice);
            if (failure is not null) return failure;
            if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId)) return Results.BadRequest();
            var challenge = await new EnrollmentChallengeStore(configuration).IssueAsync(request.DeviceId!, Operation, cancellationToken);
            return challenge.Challenge is null
                ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                : Results.Json(new { challenge = Convert.ToHexString(challenge.Challenge).ToLowerInvariant() });
        }).WithMetadata(new RequestSizeLimitAttribute(2048));

        app.MapPost("/api/device/attendance/otp/verify", async (FacultyOtpRequest request,
            HttpContext context, IConfiguration configuration, AttendanceOtpService service, CancellationToken cancellationToken) =>
        {
            var failure = CheckAccess(app, context, configuration, isAuthorizedDevice);
            if (failure is not null) return failure;
            if (!EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
                !MobileVerificationCodes.IsValid(request.Value) ||
                !FacultyOtpProtocol.TryHex(request.Challenge, out var challenge) ||
                !FacultyOtpProtocol.TryHex(request.Tag, out var tag))
                return Results.BadRequest();
            var master = FacultyOtpProtocol.ReadKey(configuration["EnrollmentSecurity:MasterKey"], "EnrollmentSecurity:MasterKey");
            byte[]? key = null;
            try
            {
                key = await new EnrollmentDeviceCredentialStore(configuration).LoadAsync(request.DeviceId!, master, cancellationToken);
                var body = Encoding.ASCII.GetBytes(request.Value + "\n");
                if (key is null || !await new EnrollmentChallengeStore(configuration).VerifyAndConsumeAsync(
                        request.DeviceId!, Operation, key, challenge, body, tag, cancellationToken))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                var accepted = await service.RedeemAsync(request.DeviceId!, request.Value!, cancellationToken);
                var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
                var responseBody = digest + "\n" + (accepted ? "accepted\n" : "denied\n");
                var responseTag = EnrollmentMessageAuthentication.ComputeTag(key, true, request.DeviceId!, Operation,
                    challenge, Encoding.ASCII.GetBytes(responseBody));
                return Results.Json(new FacultyOtpResponse(responseBody, Convert.ToHexString(responseTag).ToLowerInvariant()));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
                if (key is not null) CryptographicOperations.ZeroMemory(key);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(2048));
    }

    private static IResult? CheckAccess(WebApplication app, HttpContext context, IConfiguration configuration,
        Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!configuration.GetValue<bool>("AttendanceOtp:EnableDeviceApi"))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
            return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
        return isAuthorizedDevice(context, configuration) ? null : Results.Unauthorized();
    }
}
