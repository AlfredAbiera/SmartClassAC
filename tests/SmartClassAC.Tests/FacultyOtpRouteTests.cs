using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class FacultyOtpRouteTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task RoutesAreDisabledByDefaultAndEnforceTransportAndApiKey(string environment)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<GsmSmsTransport>();
        builder.Services.AddOptions();
        await using var app = builder.Build();
        app.MapFacultyOtpRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
        app.MapFacultyDeviceMenuRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
        app.MapAcIrRoutes((context, _) => context.Request.Headers["X-Device-Api-Key"] == "test-key");
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var paths = new[] { "request", "verify" };
        foreach (var path in paths)
        {
            using var disabledChallenge = await client.PostAsJsonAsync($"api/device/faculty/otp/{path}/challenge", new FacultyOtpChallengeRequest("1000000001"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledChallenge.StatusCode);
            Assert.True(disabledChallenge.Headers.CacheControl?.NoStore);
            using var disabled = await client.PostAsJsonAsync($"api/device/faculty/otp/{path}", new FacultyOtpRequest("1000000001", "123456", new string('0', 64), new string('0', 64)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
            Assert.True(disabled.Headers.CacheControl?.NoStore);
        }
        app.Configuration["FacultyOtp:EnableDeviceApi"] = "true";
        using var disabledIr = await client.PostAsJsonAsync("api/device/ac-ir/boot/challenge", new FacultyOtpChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledIr.StatusCode);
        Assert.True(disabledIr.Headers.CacheControl?.NoStore);
        using var disabledMenu = await client.PostAsJsonAsync("api/device/faculty/menu/login/challenge", new FacultyOtpChallengeRequest("1000000001"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledMenu.StatusCode);
        Assert.True(disabledMenu.Headers.CacheControl?.NoStore);
        app.Configuration["FacultyOtp:EnableMenuApi"] = "true";
        app.Configuration["AcIr:EnableDeviceApi"] = "true";
        using var unauthorizedIr = await client.PostAsJsonAsync("api/device/ac-ir/boot/challenge", new FacultyOtpChallengeRequest("1000000001"));
        Assert.Equal(environment == "Development" ? HttpStatusCode.Unauthorized : HttpStatusCode.UpgradeRequired, unauthorizedIr.StatusCode);
        using var menuUnauthorized = await client.PostAsJsonAsync("api/device/faculty/menu/login/challenge", new FacultyOtpChallengeRequest("1000000001"));
        Assert.Equal(environment == "Development" ? HttpStatusCode.Unauthorized : HttpStatusCode.UpgradeRequired, menuUnauthorized.StatusCode);
        using var unauthorized = await client.PostAsJsonAsync("api/device/faculty/otp/request/challenge", new FacultyOtpChallengeRequest("1000000001"));
        Assert.Equal(environment == "Development" ? HttpStatusCode.Unauthorized : HttpStatusCode.UpgradeRequired, unauthorized.StatusCode);
        client.DefaultRequestHeaders.Add("X-Device-Api-Key", "test-key");
        using var invalid = await client.PostAsJsonAsync("api/device/faculty/otp/request/challenge", new FacultyOtpChallengeRequest("bad"));
        Assert.Equal(environment == "Development" ? HttpStatusCode.BadRequest : HttpStatusCode.UpgradeRequired, invalid.StatusCode);
    }
}
