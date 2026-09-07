using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
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
    .AddCheck<SupabaseHealthCheck>("supabase");

// Supabase provides PostgreSQL. The connection string and bootstrap password
// are supplied through configuration, never hard-coded in the app.
builder.Services.Configure<DefaultAdminOptions>(builder.Configuration.GetSection("DefaultAdmin"));
builder.Services.Configure<PasswordResetOptions>(builder.Configuration.GetSection("PasswordReset"));
builder.Services.Configure<SchoolTimeOptions>(builder.Configuration.GetSection("SchoolTime"));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<LoginAttemptLimiter>();
builder.Services.AddSingleton<IPasswordResetEmailSender, SmtpPasswordResetEmailSender>();
builder.Services.AddScoped<IUserAccountRepository, SupabasePostgresUserAccountRepository>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddHostedService<DatabaseInitializationService>();

builder.Services.AddScoped<CustomAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());

var app = builder.Build();

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

app.MapRazorPages();
app.MapBlazorHub();
app.MapHealthChecks("/health");
app.MapFallbackToPage("/_Host");

app.Run();
