using MySqlConnector;

namespace SmartClassAC.Services;

public sealed class DatabaseMigrationRunner
{
    private const string ConnectionStringName = "SmartClassMariaDb";
    private readonly string _connectionString;
    private readonly ILogger<DatabaseMigrationRunner> _logger;

    public DatabaseMigrationRunner(IConfiguration configuration, ILogger<DatabaseMigrationRunner> logger)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Set ConnectionStrings:{ConnectionStringName} before running migrations.");
        }

        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>
    /// Runs all SQL migration files from the Migrations/ folder in version order.
    /// Migrations are SQL files named with a version prefix: 001_initial.sql, 002_add_column.sql, etc.
    /// </summary>
    public async Task RunMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var builder = new MySqlConnectionStringBuilder(_connectionString)
        {
            AllowUserVariables = true
        };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = @"
                CREATE TABLE IF NOT EXISTS schema_versions (
                    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    version_key VARCHAR(100) NOT NULL,
                    applied_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    script_name VARCHAR(255) NOT NULL,
                    UNIQUE KEY uq_schema_versions_version_key (version_key)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
            await setupCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var migrationDirectories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Migrations"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Migrations"))
        };

        var migrationsDirectory = migrationDirectories.FirstOrDefault(Directory.Exists);
        if (migrationsDirectory is null)
        {
            _logger.LogWarning("Migrations directory not found. Skipping migrations.");
            return;
        }

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var migrationFile in migrationFiles)
        {
            var fileName = Path.GetFileName(migrationFile);
            var versionKey = Path.GetFileNameWithoutExtension(fileName);

            await using var checkCommand = connection.CreateCommand();
            checkCommand.CommandText = "SELECT 1 FROM schema_versions WHERE version_key = @version LIMIT 1;";
            checkCommand.Parameters.AddWithValue("@version", versionKey);
            var alreadyApplied = await checkCommand.ExecuteScalarAsync(cancellationToken) is not null;

            if (alreadyApplied)
            {
                _logger.LogInformation("Migration {MigrationName} already applied, skipping.", fileName);
                continue;
            }

            try
            {
                var scriptText = await File.ReadAllTextAsync(migrationFile, cancellationToken);
                foreach (var statement in SplitSqlStatements(scriptText))
                {
                    await using var migrationCommand = connection.CreateCommand();
                    migrationCommand.CommandText = statement;
                    migrationCommand.CommandTimeout = 300;
                    await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var recordCommand = connection.CreateCommand();
                recordCommand.CommandText = "INSERT INTO schema_versions (version_key, script_name) VALUES (@version, @name);";
                recordCommand.Parameters.AddWithValue("@version", versionKey);
                recordCommand.Parameters.AddWithValue("@name", fileName);
                await recordCommand.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation("Successfully applied migration {MigrationName}.", fileName);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to apply migration {MigrationName}.", fileName);
                throw;
            }
        }

        _logger.LogInformation("All pending migrations have been applied.");
    }

    private static IEnumerable<string> SplitSqlStatements(string sql)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var rawLine in sql.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith("--", StringComparison.Ordinal) || trimmed.Length == 0)
            {
                continue;
            }

            current.AppendLine(line);
            if (trimmed.EndsWith(';'))
            {
                var statement = current.ToString().Trim().TrimEnd(';').Trim();
                if (statement.Length > 0)
                {
                    statements.Add(statement);
                }

                current.Clear();
            }
        }

        var trailing = current.ToString().Trim().TrimEnd(';').Trim();
        if (trailing.Length > 0)
        {
            statements.Add(trailing);
        }

        return statements;
    }
}