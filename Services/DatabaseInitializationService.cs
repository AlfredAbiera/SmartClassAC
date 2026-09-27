using Microsoft.Extensions.Options;

namespace SmartClassAC.Services;

public sealed class DatabaseInitializationService : IHostedService
{
    private const int MaximumAttempts = 5;
    private readonly ILogger<DatabaseInitializationService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DefaultAdminOptions> _defaultAdmin;
    private readonly PasswordHasher _passwordHasher;

    public DatabaseInitializationService(
        IServiceScopeFactory scopeFactory,
        IOptions<DefaultAdminOptions> defaultAdmin,
        PasswordHasher passwordHasher,
        ILogger<DatabaseInitializationService> logger)
    {
        _scopeFactory = scopeFactory;
        _defaultAdmin = defaultAdmin;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var admin = _defaultAdmin.Value;
        if (string.IsNullOrWhiteSpace(admin.Username) || string.IsNullOrWhiteSpace(admin.Password))
        {
            throw new InvalidOperationException(
                "Set DefaultAdmin:Username and DefaultAdmin:Password before starting SmartClass AC.");
        }

        using var scope = _scopeFactory.CreateScope();
        var migrations = scope.ServiceProvider.GetRequiredService<DatabaseMigrationRunner>();
        var repository = scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();
        var passwordHash = _passwordHasher.Hash(admin.Password);
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                await migrations.RunMigrationsAsync(cancellationToken);
                await repository.InitializeAsync(admin, passwordHash, cancellationToken);
                var demo = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
                await demo.SeedIfNeededAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < MaximumAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _logger.LogWarning(exception, "Database initialization attempt {Attempt} of {MaximumAttempts} failed. Retrying in {DelaySeconds} seconds.", attempt, MaximumAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Database initialization failed after {MaximumAttempts} attempts. The app will start in degraded state; ensure the database schema is initialized (see README.md).", MaximumAttempts);
                // Do not throw; allow the app to start without the default admin seeded.
                // This enables deployment workflows where schema migrations happen before app startup.
                return;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
