using Microsoft.AspNetCore.Components.Authorization;
using SmartClassAC.Services;

var builder = WebApplication.CreateBuilder(args);

// 🔧 LOAD USER SECRETS (required for local development)
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddAuthorizationCore();

// Supabase provides PostgreSQL. The connection string and bootstrap password
// are supplied through configuration, never hard-coded in the app.
builder.Services.Configure<DefaultAdminOptions>(builder.Configuration.GetSection("DefaultAdmin"));
builder.Services.Configure<PasswordResetOptions>(builder.Configuration.GetSection("PasswordReset"));
builder.Services.AddSingleton<PasswordHasher>();
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
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
