using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SmartClassAC.Services;

var builder = WebApplication.CreateBuilder(args);

// 🔧 LOAD USER SECRETS (required for local development)
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ProtectedSessionStorage>();
builder.Services.AddAuthorizationCore();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SmartClassAC.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login?error=access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddHealthChecks()
    .AddCheck<MariaDbHealthCheck>("mariadb");

// MariaDB (XAMPP). The connection string and bootstrap password are supplied
// through configuration, never hard-coded in the app.
builder.Services.Configure<DefaultAdminOptions>(builder.Configuration.GetSection("DefaultAdmin"));
builder.Services.Configure<PasswordResetOptions>(builder.Configuration.GetSection("PasswordReset"));
builder.Services.Configure<SchoolTimeOptions>(builder.Configuration.GetSection("SchoolTime"));
builder.Services.Configure<DeviceApiOptions>(builder.Configuration.GetSection("DeviceApi"));
builder.Services.Configure<DemoDataOptions>(builder.Configuration.GetSection("DemoData"));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<LoginAttemptLimiter>();
builder.Services.AddSingleton<IPasswordResetEmailSender, SmtpPasswordResetEmailSender>();
builder.Services.AddScoped<IUserAccountRepository, MariaDbUserAccountRepository>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<DeviceFingerprintService>();
builder.Services.AddScoped<DemoDataSeeder>();
builder.Services.AddScoped<DatabaseMigrationRunner>();
// DatabaseInitializationService attempts to seed the default admin on startup.
// It gracefully handles schema-not-ready errors so the app can start in a degraded state.
builder.Services.AddHostedService<DatabaseInitializationService>();
builder.Services.AddHostedService<AttendanceReconciliationService>();

builder.Services.AddScoped<CustomAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());

var app = builder.Build();

// Optional: Run database migrations on startup if the Migrations folder exists.
// To enable, call: await app.Services.GetRequiredService<DatabaseMigrationRunner>().RunMigrationsAsync();
// For now, this is a manual step during deployment (see README.md).
if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Migrations")))
{
    var migrationRunner = app.Services.GetRequiredService<ILogger<DatabaseMigrationRunner>>();
    app.Logger.LogInformation("Migrations folder found. Use 'dotnet run --run-migrations' or manual execution during deployment.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/auth/login", async (HttpContext context, IUserAccountRepository accounts, PasswordHasher passwordHasher, LoginAttemptLimiter loginAttemptLimiter, CancellationToken cancellationToken) =>
{
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var username = form["username"].ToString().Trim();
    var password = form["password"].ToString();
    if (await accounts.IsInitialAdminSetupRequiredAsync(cancellationToken))
    {
        return Results.Redirect("/first-access");
    }

    if (string.IsNullOrWhiteSpace(username) || loginAttemptLimiter.IsBlocked(username))
    {
        return Results.Redirect("/login?error=signin");
    }

    var account = await accounts.FindByIdentifierAsync(username, cancellationToken);
    if (account is null || !passwordHasher.Verify(password, account.PasswordHash))
    {
        loginAttemptLimiter.RecordFailure(username);
        return Results.Redirect("/login?error=signin");
    }

    loginAttemptLimiter.RecordSuccess(username);
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
        new Claim(ClaimTypes.Name, account.DisplayName),
        new Claim(ClaimTypes.Role, account.Role.ToString())
    };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Redirect(account.Role == UserRole.Admin ? "/admin" : "/teacher");
});
app.MapGet("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    var returnUrl = context.Request.Query["returnUrl"].ToString();
    var safeReturnUrl = returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/login";
    return Results.Redirect(safeReturnUrl);
});

app.MapPost("/api/device/status", async (
    DeviceStatusRequest request,
    HttpContext context,
    DeviceFingerprintService fingerprints,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedDevice(context, configuration))
    {
        return Results.Json(new { ok = false, error = "Unauthorized device.", message = "Unauthorized device." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    try
    {
        var result = await fingerprints.GetStatusAsync(request.DeviceCode ?? string.Empty, cancellationToken);
        if (!result.Ok)
        {
            return Results.Json(new
            {
                ok = false,
                error = result.Error,
                message = result.Error
            }, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new
        {
            ok = true,
            deviceCode = result.DeviceCode,
            classroomId = result.ClassroomId,
            classroomName = result.ClassroomName,
            acStatus = result.AcStatus,
            acOn = result.AcOn,
            sessionActive = result.SessionActive,
            teacherAccountId = result.TeacherAccountId,
            teacherDisplayName = result.TeacherDisplayName,
            scheduleId = result.ScheduleId
        });
    }
    catch (Exception exception)
    {
        loggerFactory.CreateLogger("DeviceStatus").LogError(exception, "Device status failed for device {DeviceCode}", request.DeviceCode);
        return Results.Json(new
        {
            ok = false,
            message = exception.Message,
            error = exception.Message
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/device/fingerprint/enroll", async (
    EnrollFingerprintRequest request,
    HttpContext context,
    DeviceFingerprintService fingerprints,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedDevice(context, configuration))
    {
        return Results.Json(new { ok = false, error = "Unauthorized device." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = await fingerprints.EnrollAsync(request, cancellationToken);
    if (!result.Ok)
    {
        var status = result.Error?.Contains("already", StringComparison.OrdinalIgnoreCase) == true
            || result.Error?.Contains("full", StringComparison.OrdinalIgnoreCase) == true
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status400BadRequest;
        return Results.Json(new { ok = false, error = result.Error }, statusCode: status);
    }

    return Results.Json(new
    {
        ok = true,
        teacherAccountId = result.TeacherAccountId,
        displayName = result.DisplayName,
        username = result.Username,
        employeeNumber = result.EmployeeNumber,
        devicePin = result.DevicePin,
        email = result.Email,
        phone = result.Phone,
        fingerprintTemplateCount = result.FingerprintTemplateCount,
        templateId = result.TemplateId,
        fingerPosition = result.FingerPosition,
        classroomId = result.ClassroomId,
        classroomName = result.ClassroomName,
        deviceCode = result.DeviceCode
    });
});

app.MapPost("/api/device/fingerprint/scan", async (
    DeviceScanRequest request,
    HttpContext context,
    DeviceFingerprintService fingerprints,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedDevice(context, configuration))
    {
        return Results.Json(new { ok = false, action = "unauthorized", error = "Unauthorized device.", message = "Unauthorized device." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    try
    {
        var result = await fingerprints.ScanAsync(request.TemplateId, request.DeviceCode ?? string.Empty, cancellationToken);
        var status = result.Action switch
        {
            "unknown" => StatusCodes.Status404NotFound,
            "unknown_device" => StatusCodes.Status404NotFound,
            "wrong_room" => StatusCodes.Status409Conflict,
            "no_schedule" => StatusCodes.Status409Conflict,
            "error" => StatusCodes.Status409Conflict,
            "unauthorized" => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status200OK
        };

        return Results.Json(new
        {
            ok = result.Ok,
            action = result.Action,
            teacherAccountId = result.TeacherAccountId,
            teacherDisplayName = result.TeacherDisplayName,
            scheduleId = result.ScheduleId,
            classroomId = result.ClassroomId,
            classroomName = result.ClassroomName,
            deviceCode = result.DeviceCode,
            message = result.Message,
            error = result.Message
        }, statusCode: status);
    }
    catch (Exception exception)
    {
        loggerFactory.CreateLogger("DeviceFingerprintScan").LogError(exception, "Device scan failed for template {TemplateId} device {DeviceCode}", request.TemplateId, request.DeviceCode);
        return Results.Json(new
        {
            ok = false,
            action = "error",
            message = exception.Message,
            error = exception.Message
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapRazorPages();
app.MapBlazorHub();
app.MapHealthChecks("/health");
app.MapFallbackToPage("/_Host");

app.Run();

static bool IsAuthorizedDevice(HttpContext context, IConfiguration configuration)
{
    var expected = configuration["DeviceApi:ApiKey"];
    if (string.IsNullOrWhiteSpace(expected))
    {
        return false;
    }

    if (!context.Request.Headers.TryGetValue("X-Device-Api-Key", out var provided))
    {
        return false;
    }

    var providedKey = provided.ToString();
    return providedKey.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedKey),
            Encoding.UTF8.GetBytes(expected));
}