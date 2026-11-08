using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace SmartClassAC.Services;

internal sealed record FacultyMenuRequest(string? DeviceId, string? Value, int Index, string? Challenge, string? Tag, long RoomId = 0);

internal static class FacultyDeviceMenuRoutes
{
    public static void MapFacultyDeviceMenuRoutes(this WebApplication app, Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        MapChallenge(app, "/api/device/faculty/menu/login/challenge", "faculty_menu_login", isAuthorizedDevice);
        MapChallenge(app, "/api/device/faculty/menu/schedule/challenge", "faculty_menu_schedule", isAuthorizedDevice);
        MapChallenge(app, "/api/device/faculty/menu/ac/challenge", "faculty_menu_ac", isAuthorizedDevice);
        MapChallenge(app, "/api/device/faculty/menu/ac-v2/challenge", "faculty_menu_ac_v2", isAuthorizedDevice);
        MapChallenge(app, "/api/device/faculty/menu/ac-target/challenge", "faculty_menu_ac_target", isAuthorizedDevice);
        MapOperation(app, "/api/device/faculty/menu/login", "faculty_menu_login", isAuthorizedDevice);
        MapOperation(app, "/api/device/faculty/menu/schedule", "faculty_menu_schedule", isAuthorizedDevice);
        MapOperation(app, "/api/device/faculty/menu/ac", "faculty_menu_ac", isAuthorizedDevice);
        MapOperation(app, "/api/device/faculty/menu/ac-v2", "faculty_menu_ac_v2", isAuthorizedDevice);
        MapOperation(app, "/api/device/faculty/menu/ac-target", "faculty_menu_ac_target", isAuthorizedDevice);
    }

    private static IResult? CheckAccess(WebApplication app, HttpContext context, IConfiguration configuration,
        Func<HttpContext, IConfiguration, bool> isAuthorizedDevice)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!configuration.GetValue<bool>("FacultyOtp:EnableDeviceApi") ||
            !configuration.GetValue<bool>("FacultyOtp:EnableMenuApi"))
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
            if (operation == "faculty_menu_ac_target" && !configuration.GetValue<bool>("FacultyOtp:EnableAcTargetApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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
        app.MapPost(path, async (FacultyMenuRequest request, HttpContext context, IConfiguration configuration,
            IOptions<SchoolTimeOptions> schoolTime, CancellationToken cancellationToken) =>
        {
            var failure = CheckAccess(app, context, configuration, isAuthorizedDevice);
            if (failure is not null) return failure;
            if (operation == "faculty_menu_ac_target" && !configuration.GetValue<bool>("FacultyOtp:EnableAcTargetApi"))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var response = await new FacultyDeviceMenuProtocol(configuration, schoolTime)
                .ExecuteAsync(operation, request, cancellationToken);
            return response is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(response);
        }).WithMetadata(new RequestSizeLimitAttribute(2048));
    }
}

internal sealed class FacultyDeviceMenuProtocol(IConfiguration configuration, IOptions<SchoolTimeOptions> schoolTime)
{
    public async Task<FacultyOtpResponse?> ExecuteAsync(string operation, FacultyMenuRequest request,
        CancellationToken cancellationToken = default)
    {
        bool login = operation == "faculty_menu_login";
        bool ac = operation is "faculty_menu_ac" or "faculty_menu_ac_v2";
        bool target = operation == "faculty_menu_ac_target";
        int expected = request.Index / 100, desired = request.Index % 100;
        if ((!login && !ac && !target && operation != "faculty_menu_schedule") ||
            !EnrollmentMessageAuthentication.IsValidDeviceId(request.DeviceId) ||
            (target ? request.RoomId <= 0 || expected is < 16 or > 30 || desired is < 16 or > 30 ||
                      Math.Abs(expected - desired) != 1
                    : request.RoomId != 0 || request.Index is < 0 or > 999 || ((login || ac) && request.Index != 0)) ||
            (login ? request.Value is not { Length: 6 } || !request.Value.All(char.IsAsciiDigit)
                   : !FacultyOtpProtocol.TryHex(request.Value, out _)) ||
            !FacultyOtpProtocol.TryHex(request.Challenge, out var challenge) ||
            !FacultyOtpProtocol.TryHex(request.Tag, out var tag))
            return null;

        var secret = FacultyOtpProtocol.ReadKey(configuration["FacultyOtp:Secret"], "FacultyOtp:Secret");
        byte[]? master = null, deviceKey = null;
        try
        {
            master = FacultyOtpProtocol.ReadKey(configuration["EnrollmentSecurity:MasterKey"], "EnrollmentSecurity:MasterKey");
            deviceKey = await new EnrollmentDeviceCredentialStore(configuration).LoadAsync(request.DeviceId!, master, cancellationToken);
            if (deviceKey is null) return null;
            var body = Encoding.ASCII.GetBytes(target
                ? FormattableString.Invariant($"{request.Value}\n{request.Index}\n{request.RoomId}\n")
                : FormattableString.Invariant($"{request.Value}\n{request.Index}\n"));
            if (!await new EnrollmentChallengeStore(configuration).VerifyAndConsumeAsync(
                request.DeviceId!, operation, deviceKey, challenge, body, tag, cancellationToken))
                return null;

            string content;
            if (login)
            {
                var session = await new FacultyOtpStore(configuration)
                    .VerifyAndCreateSessionAsync(request.DeviceId!, request.Value!, secret, cancellationToken);
                content = session is null ? "denied\n" : "accepted\n" + session + "\n";
            }
            else if (target)
            {
                var result = await new FacultyDeviceAcStore(configuration).SetTargetAsync(
                    request.DeviceId!, request.Value!, secret, request.RoomId, expected, desired, schoolTime.Value, cancellationToken);
                content = result switch
                {
                    FacultyDeviceTargetResult.Updated => "saved\n",
                    FacultyDeviceTargetResult.Denied => "denied\n",
                    FacultyDeviceTargetResult.NoActiveClass => "no_class\n",
                    FacultyDeviceTargetResult.BelowMinimum => "minimum\n",
                    FacultyDeviceTargetResult.StaleTarget => "stale\n",
                    _ => throw new InvalidOperationException("Validated AC target was rejected as invalid.")
                };
            }
            else if (ac)
            {
                var info = await new FacultyDeviceAcStore(configuration)
                    .GetAsync(request.DeviceId!, request.Value!, secret, cancellationToken);
                content = info is null ? "denied\n" : operation == "faculty_menu_ac_v2"
                    ? FormattableString.Invariant($"info\n{info.ClassroomId}\n{info.Target}\n{info.Minimum}\n{(info.HasActiveClass ? 1 : 0)}\n")
                    : FormattableString.Invariant($"info\n{info.Target}\n{info.Minimum}\n{(info.HasActiveClass ? 1 : 0)}\n");
            }
            else
            {
                var schedules = await new FacultyDeviceScheduleStore(configuration, schoolTime)
                    .GetTodayAsync(request.DeviceId!, request.Value!, secret, cancellationToken);
                if (schedules is null) content = "denied\n";
                else if (request.Index >= schedules.Count) content = "end\n";
                else
                {
                    var item = schedules[request.Index];
                    content = string.Concat("item\n", request.Index.ToString(CultureInfo.InvariantCulture), "\n",
                        schedules.Count.ToString(CultureInfo.InvariantCulture), "\n", Display(item.Subject), "\n",
                        Display(item.Classroom), "\n", item.Start.ToString(@"hh\:mm", CultureInfo.InvariantCulture), "\n",
                        item.End.ToString(@"hh\:mm", CultureInfo.InvariantCulture), "\n");
                }
            }
            var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var responseBody = digest + "\n" + content;
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

    private static string Display(string text)
    {
        var result = new StringBuilder(16);
        foreach (var rune in text.EnumerateRunes())
        {
            if (result.Length == 16) break;
            result.Append(rune.Value is >= 32 and <= 126 ? (char)rune.Value : '?');
        }
        return result.ToString();
    }
}
