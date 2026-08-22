using Microsoft.Extensions.Options;

namespace SmartClassAC.Services;

public sealed class DatabaseInitializationService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DefaultAdminOptions> _defaultAdmin;
    private readonly PasswordHasher _passwordHasher;

    public DatabaseInitializationService(
        IServiceScopeFactory scopeFactory,
        IOptions<DefaultAdminOptions> defaultAdmin,
        PasswordHasher passwordHasher)
    {
        _scopeFactory = scopeFactory;
        _defaultAdmin = defaultAdmin;
        _passwordHasher = passwordHasher;
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
        var repository = scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();
        await repository.InitializeAsync(admin, _passwordHasher.Hash(admin.Password), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
