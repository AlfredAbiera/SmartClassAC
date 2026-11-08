using MySqlConnector;
using System.Security.Cryptography;
using System.Text;

namespace SmartClassAC.Services;

/// <summary>
/// Server-side persistence for the local MariaDB database (XAMPP). Credentials stay on the server.
/// </summary>
public sealed class MariaDbUserAccountRepository : IUserAccountRepository
{
    private const string ConnectionStringName = "SmartClassMariaDb";
    private readonly string _connectionString;
    private readonly SchoolTimeOptions _schoolTime;

    public MariaDbUserAccountRepository(IConfiguration configuration, Microsoft.Extensions.Options.IOptions<SchoolTimeOptions> schoolTime)
    {
        var configuredConnectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            throw new InvalidOperationException(
                "Set ConnectionStrings:SmartClassMariaDb with a MariaDB connection string (e.g. Server=127.0.0.1;Port=3306;Database=smartclassac;User ID=root;Password=;).");
        }

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(configuredConnectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The MariaDB connection string is invalid.", exception);
        }

        if (string.IsNullOrWhiteSpace(builder.Server))
        {
            throw new InvalidOperationException("The MariaDB connection string must include its server/host.");
        }

        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new InvalidOperationException("The MariaDB connection string must include a database name.");
        }

        _connectionString = builder.ConnectionString;
        _schoolTime = schoolTime.Value;
    }

    public async Task InitializeAsync(DefaultAdminOptions defaultAdmin, string passwordHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ApplyBundledSchemaAsync(connection, cancellationToken);
        await SeedDefaultAdminAsync(connection, defaultAdmin, passwordHash, cancellationToken);
    }

    private static async Task ApplyBundledSchemaAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "001_initial_schema.sql");
        if (!File.Exists(path))
        {
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Migrations", "001_initial_schema.sql"));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "MariaDB schema script was not found. Ensure Migrations/001_initial_schema.sql is copied to the output directory.",
                path);
        }

        var sql = await File.ReadAllTextAsync(path, cancellationToken);
        await ExecuteSqlBatchAsync(connection, sql, cancellationToken);
    }

    private static async Task ExecuteSqlBatchAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        foreach (var statement in SplitSqlStatements(sql))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static IEnumerable<string> SplitSqlStatements(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
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

    public async Task CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<UserAccountRecord?> FindByIdentifierAsync(string usernameOrEmail, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, username, display_name, email, password_hash, role, is_default_admin
            FROM user_accounts
            WHERE (LOWER(username) = LOWER(@identifier) OR LOWER(email) = LOWER(@identifier))
              AND is_active = TRUE
            LIMIT 1;";
        command.Parameters.Add("@identifier", MySqlDbType.VarChar).Value = usernameOrEmail;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new UserAccountRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), Enum.Parse<UserRole>(reader.GetString(5)), reader.GetBoolean(6))
            : null;
    }

    public async Task<UserAccountRecord?> FindByIdAsync(long accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, username, display_name, email, password_hash, role, is_default_admin
            FROM user_accounts
            WHERE id = @id AND is_active = TRUE;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new UserAccountRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), Enum.Parse<UserRole>(reader.GetString(5)), reader.GetBoolean(6))
            : null;
    }

    public async Task<bool> IsInitialAdminSetupRequiredAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM user_accounts WHERE is_default_admin = TRUE AND email IS NULL) AS required;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0;
    }

    public async Task<bool> SetInitialAdminEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE user_accounts SET email = @email WHERE is_default_admin = TRUE AND email IS NULL;";
        command.Parameters.Add("@email", MySqlDbType.VarChar).Value = email;
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return false;
        }
    }

    public async Task<OperationResult> UpdateDefaultAdminCredentialsAsync(long accountId, string email, string? passwordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = passwordHash is null
            ? "UPDATE user_accounts SET email = @email WHERE id = @id AND is_default_admin = TRUE;"
            : "UPDATE user_accounts SET email = @email, password_hash = @passwordHash WHERE id = @id AND is_default_admin = TRUE;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
        command.Parameters.Add("@email", MySqlDbType.VarChar).Value = email;
        if (passwordHash is not null) command.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash;
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OperationResult.Success()
                : OperationResult.Failure("The default administrator account was not found.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("That email is already registered to another account.");
        }
    }

    public async Task<OperationResult> CreateTeacherAsync(CreateTeacherRequest request, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var account = connection.CreateCommand();
            account.Transaction = transaction;
            account.CommandText = @"
                INSERT INTO user_accounts (username, display_name, employee_number, device_pin, email, phone, password_hash, role)
                VALUES (@username, @displayName, @employeeNumber, @devicePin, @email, @phone, @passwordHash, 'Teacher');";
            account.Parameters.Add("@username", MySqlDbType.VarChar).Value = request.Username;
            account.Parameters.Add("@displayName", MySqlDbType.VarChar).Value = request.DisplayName;
            account.Parameters.Add("@employeeNumber", MySqlDbType.VarChar).Value = request.EmployeeNumber;
            account.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = await ResolveNewTeacherPinAsync(connection, transaction, request.DevicePin, cancellationToken);
            account.Parameters.AddWithValue("@email", (object?)request.Email ?? DBNull.Value);
            account.Parameters.Add("@phone", MySqlDbType.VarChar).Value = request.Phone;
            account.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash;
            await account.ExecuteNonQueryAsync(cancellationToken);
            var teacherId = account.LastInsertedId;
            if (teacherId <= 0)
            {
                throw new InvalidOperationException("MariaDB did not return the new teacher ID.");
            }

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("That username, employee number, PIN, email, or mobile number is already registered.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("PIN", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure(exception.Message);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<OperationResult> UpdateAdminTeacherAsync(UpdateAdminTeacherRequest request, string? passwordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            string? devicePin = null;
            if (!string.IsNullOrWhiteSpace(request.DevicePin))
            {
                devicePin = DevicePinCodes.Normalize(request.DevicePin);
                if (!DevicePinCodes.IsValid(devicePin))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure(DevicePinCodes.RequirementMessage);
                }

                if (await DevicePinExistsAsync(connection, transaction, devicePin, request.AccountId, cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("That device PIN is already registered to another account.");
                }
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (passwordHash is null && devicePin is null)
            {
                command.CommandText = @"
                    UPDATE user_accounts
                    SET display_name = @displayName, username = @username, employee_number = @employeeNumber, email = @email, phone = @phone
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
            }
            else if (passwordHash is not null && devicePin is null)
            {
                command.CommandText = @"
                    UPDATE user_accounts
                    SET display_name = @displayName, username = @username, employee_number = @employeeNumber, email = @email, phone = @phone, password_hash = @passwordHash
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
                command.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash;
            }
            else if (passwordHash is null && devicePin is not null)
            {
                command.CommandText = @"
                    UPDATE user_accounts
                    SET display_name = @displayName, username = @username, employee_number = @employeeNumber, email = @email, phone = @phone, device_pin = @devicePin
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
                command.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = devicePin;
            }
            else
            {
                command.CommandText = @"
                    UPDATE user_accounts
                    SET display_name = @displayName, username = @username, employee_number = @employeeNumber, email = @email, phone = @phone, device_pin = @devicePin, password_hash = @passwordHash
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
                command.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = devicePin!;
                command.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash!;
            }

            command.Parameters.Add("@id", MySqlDbType.Int64).Value = request.AccountId;
            command.Parameters.Add("@displayName", MySqlDbType.VarChar).Value = request.DisplayName;
            command.Parameters.Add("@username", MySqlDbType.VarChar).Value = request.Username;
            command.Parameters.Add("@employeeNumber", MySqlDbType.VarChar).Value = request.EmployeeNumber;
            command.Parameters.AddWithValue("@email", (object?)request.Email ?? DBNull.Value);
            command.Parameters.Add("@phone", MySqlDbType.VarChar).Value = request.Phone;

            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("The teacher account was not found.");
            }

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("That username, employee number, PIN, email, or mobile number is already registered.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<OperationResult> SetTeacherDevicePinByEmployeeNumberAsync(string employeeNumber, string pinCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeNumber) || !DevicePinCodes.IsValid(pinCode))
        {
            return OperationResult.Failure(DevicePinCodes.RequirementMessage);
        }

        var pin = DevicePinCodes.Normalize(pinCode);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE user_accounts
            SET device_pin = @pin
            WHERE employee_number = @employeeNumber
              AND role = 'Teacher'
              AND is_active = TRUE;";
        command.Parameters.Add("@pin", MySqlDbType.VarChar).Value = pin;
        command.Parameters.Add("@employeeNumber", MySqlDbType.VarChar).Value = employeeNumber.Trim();
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OperationResult.Success()
                : OperationResult.Failure("Teacher not found for that employee number.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("That device PIN is already registered to another account.");
        }
    }

    public async Task<IReadOnlyList<TeacherDirectoryEntry>> GetTeachersAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<TeacherDirectoryEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT u.id, u.display_name, u.username, u.employee_number, u.email, u.phone, COUNT(f.id), u.is_active
            FROM user_accounts u LEFT JOIN fingerprint_templates f ON f.user_account_id = u.id
            WHERE u.role = 'Teacher'
            GROUP BY u.id, u.display_name, u.username, u.employee_number, u.email, u.phone, u.is_active
            ORDER BY u.is_active DESC, u.display_name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new TeacherDirectoryEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Convert.ToInt32(reader.GetValue(6)),
                reader.GetBoolean(7)));
        }
        return items;
    }

    public async Task<OperationResult> DeleteTeacherAsync(long accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using (var scheduled = connection.CreateCommand())
        {
            scheduled.CommandText = @"
                SELECT EXISTS(
                    SELECT 1 FROM class_schedules
                    WHERE teacher_account_id = @id AND schedule_date >= CURRENT_DATE
                );";
            scheduled.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
            if (Convert.ToInt64(await scheduled.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
            {
                return OperationResult.Failure("Delete or reassign this teacher's current and future schedules before removing the account.");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM user_accounts
            WHERE id = @id AND role = 'Teacher';";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OperationResult.Success()
                : OperationResult.Failure("The teacher account was not found.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.RowIsReferenced2)
        {
            return OperationResult.Failure("This teacher has an unresolved fingerprint write or other dependent records.");
        }
    }

    public async Task<OperationResult> SetTeacherActiveAsync(long accountId, bool isActive, CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return OperationResult.Failure("Select a teacher account.");
        }

        if (!isActive)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await LockFingerprintDevicesAsync(connection, transaction, cancellationToken);
            if (await HasFingerprintWriteForAsync(connection, transaction, "p.teacher_account_id", accountId, cancellationToken))
                return OperationResult.Failure("This teacher has an unresolved fingerprint write.");
            await using (var scheduled = connection.CreateCommand())
            {
                scheduled.Transaction = transaction;
                scheduled.CommandText = @"
                    SELECT EXISTS(
                        SELECT 1 FROM class_schedules
                        WHERE teacher_account_id = @id AND schedule_date >= CURRENT_DATE
                    );";
                scheduled.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
                if (Convert.ToInt64(await scheduled.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
                {
                    return OperationResult.Failure("Delete or reassign this teacher's current and future schedules before deactivating the account.");
                }
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                UPDATE user_accounts
                SET is_active = FALSE
                WHERE id = @id AND role = 'Teacher';";
            command.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return OperationResult.Failure("The teacher account was not found.");
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }

        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE user_accounts
                SET is_active = TRUE
                WHERE id = @id AND role = 'Teacher';";
            command.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OperationResult.Success()
                : OperationResult.Failure("The teacher account was not found.");
        }
    }

    public async Task<IReadOnlyList<ClassroomEntry>> GetClassroomsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<ClassroomEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, name, capacity, target_temperature, current_temperature, ac_status, is_active, minimum_temperature
            FROM classrooms WHERE is_active = TRUE ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ClassroomEntry(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.GetString(5), reader.GetBoolean(6), reader.GetInt32(7)));
        }
        return items;
    }

    public async Task<OperationResult> CreateClassroomAsync(CreateClassroomRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO classrooms (name, capacity, target_temperature, current_temperature, ac_status, minimum_temperature)
                VALUES (@name, @capacity, @targetTemperature, NULL, 'Idle', @minimumTemperature);";
            AddClassroomParameters(command, request.Name, request.Capacity, request.TargetTemperature);
            command.Parameters.Add("@minimumTemperature", MySqlDbType.Int32).Value = request.MinimumTemperature;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("A classroom with that name already exists.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<OperationResult> UpdateClassroomAsync(UpdateClassroomRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!request.IsActive)
        {
            await LockFingerprintDevicesAsync(connection, transaction, cancellationToken);
            if (await HasFingerprintWriteForAsync(connection, transaction, "d.classroom_id", request.Id, cancellationToken))
                return OperationResult.Failure("This classroom has an unresolved fingerprint write.");
            if (await HasInFlightIrForRoomAsync(connection, transaction, request.Id, cancellationToken))
                return OperationResult.Failure("This classroom has an unresolved IR command. Stop and reconcile the scanner before retiring it.");
            await InvalidateIrForRoomAsync(connection, transaction, request.Id, cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            UPDATE classrooms
            SET name = @name, capacity = @capacity, target_temperature = @targetTemperature, is_active = @isActive, minimum_temperature = @minimumTemperature
            WHERE id = @id;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = request.Id;
        AddClassroomParameters(command, request.Name, request.Capacity, request.TargetTemperature);
        command.Parameters.Add("@minimumTemperature", MySqlDbType.Int32).Value = request.MinimumTemperature;
        command.Parameters.Add("@isActive", MySqlDbType.Bool).Value = request.IsActive;
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return OperationResult.Failure("The classroom was not found.");
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("A classroom with that name already exists.");
        }
    }

    public async Task<OperationResult> DeleteClassroomAsync(long classroomId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFingerprintDevicesAsync(connection, transaction, cancellationToken);
        if (await HasFingerprintWriteForAsync(connection, transaction, "d.classroom_id", classroomId, cancellationToken))
            return OperationResult.Failure("This classroom has an unresolved fingerprint write.");
        if (await HasInFlightIrForRoomAsync(connection, transaction, classroomId, cancellationToken))
            return OperationResult.Failure("This classroom has an unresolved IR command. Stop and reconcile the scanner before retiring it.");

        await using (var scheduled = connection.CreateCommand())
        {
            scheduled.Transaction = transaction;
            scheduled.CommandText = @"
                SELECT EXISTS(
                    SELECT 1 FROM class_schedules
                    WHERE classroom_id = @id AND schedule_date >= CURRENT_DATE
                );";
            scheduled.Parameters.Add("@id", MySqlDbType.Int64).Value = classroomId;
            if (Convert.ToInt64(await scheduled.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
            {
                return OperationResult.Failure("Delete or move this classroom's current and future schedules before removing the room.");
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            UPDATE classrooms
            SET is_active = FALSE, ac_status = 'Retired'
            WHERE id = @id AND is_active = TRUE;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = classroomId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            return OperationResult.Failure("The active classroom was not found.");
        await InvalidateIrForRoomAsync(connection, transaction, classroomId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CreateScheduleAsync(CreateScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var result = await CreateSchedulesAsync(request with { RecurrencePattern = ScheduleRecurrence.None }, cancellationToken);
        return result.Succeeded
            ? OperationResult.Success()
            : OperationResult.Failure(result.Error ?? "Schedule could not be saved.");
    }

    public async Task<CreateSchedulesResult> CreateSchedulesAsync(CreateScheduleRequest request, CancellationToken cancellationToken = default)
    {
        if (!ScheduleKinds.IsValid(request.ScheduleKind))
        {
            return CreateSchedulesResult.Failure("Schedule kind must be Regular or Makeup.");
        }

        if (request.SemesterId <= 0)
        {
            return CreateSchedulesResult.Failure("Select a semester for this schedule.");
        }

        var pattern = string.IsNullOrWhiteSpace(request.RecurrencePattern)
            ? ScheduleRecurrence.None
            : request.RecurrencePattern.Trim();
        if (!ScheduleRecurrence.IsValid(pattern))
        {
            return CreateSchedulesResult.Failure("Choose a valid repeat pattern.");
        }

        if (pattern == ScheduleRecurrence.Custom && ScheduleRecurrence.ResolveWeekdays(pattern, request.ScheduleDate, request.CustomWeekdays).Count == 0)
        {
            return CreateSchedulesResult.Failure("Select at least one weekday for a custom repeating schedule.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            DateOnly semesterStart;
            DateOnly semesterEnd;
            await using (var semester = connection.CreateCommand())
            {
                semester.Transaction = transaction;
                semester.CommandText = @"
                    SELECT start_date, end_date, is_active
                    FROM semesters
                    WHERE id = @semesterId
                    FOR UPDATE;";
                semester.Parameters.Add("@semesterId", MySqlDbType.Int64).Value = request.SemesterId;
                await using var reader = await semester.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await reader.DisposeAsync();
                    await transaction.RollbackAsync(cancellationToken);
                    return CreateSchedulesResult.Failure("The selected semester was not found.");
                }

                semesterStart = reader.GetFieldValue<DateOnly>(0);
                semesterEnd = reader.GetFieldValue<DateOnly>(1);
                var isActive = reader.GetBoolean(2);
                await reader.DisposeAsync();

                if (!isActive)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return CreateSchedulesResult.Failure("The selected semester is inactive.");
                }

                if (request.ScheduleDate < semesterStart || request.ScheduleDate > semesterEnd)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return CreateSchedulesResult.Failure("The schedule start date must fall within the semester date range.");
                }
            }

            var dates = ScheduleRecurrence.ExpandDates(request.ScheduleDate, semesterEnd, pattern, request.CustomWeekdays);
            if (dates.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return CreateSchedulesResult.Failure("No schedule dates fall between the start date and the semester end.");
            }

            await using (var enrolled = connection.CreateCommand())
            {
                enrolled.Transaction = transaction;
                enrolled.CommandText = @"
                    SELECT EXISTS(
                        SELECT 1 FROM teacher_semesters
                        WHERE teacher_account_id = @teacherId AND semester_id = @semesterId
                    );";
                enrolled.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = request.TeacherAccountId;
                enrolled.Parameters.Add("@semesterId", MySqlDbType.Int64).Value = request.SemesterId;
                if (Convert.ToInt64(await enrolled.ExecuteScalarAsync(cancellationToken) ?? 0L) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return CreateSchedulesResult.Failure("Enroll the teacher in this semester before creating a schedule.");
                }
            }

            await using (var actors = connection.CreateCommand())
            {
                actors.Transaction = transaction;
                actors.CommandText = @"
                    SELECT EXISTS(
                        SELECT 1
                        FROM user_accounts teacher CROSS JOIN classrooms classroom
                        WHERE teacher.id = @teacherId AND teacher.role = 'Teacher' AND teacher.is_active = TRUE
                          AND classroom.id = @classroomId AND classroom.is_active = TRUE
                    );";
                actors.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = request.TeacherAccountId;
                actors.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = request.ClassroomId;
                if (Convert.ToInt64(await actors.ExecuteScalarAsync(cancellationToken) ?? 0L) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return CreateSchedulesResult.Failure("Select an active teacher and an active classroom.");
                }
            }

            var created = 0;
            var skipped = 0;
            foreach (var date in dates)
            {
                var dayRequest = request with { ScheduleDate = date, RecurrencePattern = ScheduleRecurrence.None, CustomWeekdays = null };

                await using var conflict = connection.CreateCommand();
                conflict.Transaction = transaction;
                conflict.CommandText = @"
                    SELECT EXISTS(
                        SELECT 1 FROM class_schedules
                        WHERE schedule_date = @date
                          AND (teacher_account_id = @teacherId OR classroom_id = @classroomId)
                          AND start_time < @endTime AND end_time > @startTime
                    );";
                AddScheduleParameters(conflict, dayRequest);
                if (Convert.ToInt64(await conflict.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
                {
                    skipped++;
                    continue;
                }

                await using var create = connection.CreateCommand();
                create.Transaction = transaction;
                create.CommandText = @"
                    INSERT INTO class_schedules (
                        teacher_account_id, original_teacher_account_id, classroom_id, classroom_name,
                        semester_id, schedule_kind, subject_name, schedule_date, start_time, end_time)
                    SELECT teacher.id, teacher.id, classroom.id, classroom.name,
                           @semesterId, @scheduleKind, @subject, @date, @startTime, @endTime
                    FROM user_accounts teacher CROSS JOIN classrooms classroom
                    WHERE teacher.id = @teacherId AND teacher.role = 'Teacher' AND teacher.is_active = TRUE
                      AND classroom.id = @classroomId AND classroom.is_active = TRUE;";
                AddScheduleParameters(create, dayRequest);
                if (await create.ExecuteNonQueryAsync(cancellationToken) == 1)
                {
                    created++;
                }
                else
                {
                    skipped++;
                }
            }

            if (created == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return CreateSchedulesResult.Failure(
                    dates.Count == 1
                        ? "The selected teacher or classroom already has an overlapping schedule."
                        : "All generated dates conflict with existing teacher or classroom schedules.");
            }

            await transaction.CommitAsync(cancellationToken);
            return CreateSchedulesResult.Success(created, skipped);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<SemesterEntry>> GetSemestersAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<SemesterEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, name, start_date, end_date, is_active
            FROM semesters
            ORDER BY start_date DESC, name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SemesterEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetFieldValue<DateOnly>(2),
                reader.GetFieldValue<DateOnly>(3),
                reader.GetBoolean(4)));
        }
        return items;
    }

    public async Task<OperationResult> CreateSemesterAsync(CreateSemesterRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
        {
            return OperationResult.Failure("Enter a semester name of 120 characters or fewer.");
        }

        if (request.EndDate < request.StartDate)
        {
            return OperationResult.Failure("Semester end date must be on or after the start date.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO semesters (name, start_date, end_date, is_active)
            VALUES (@name, @startDate, @endDate, @isActive);";
        command.Parameters.Add("@name", MySqlDbType.VarChar).Value = request.Name.Trim();
        command.Parameters.Add("@startDate", MySqlDbType.Date).Value = request.StartDate;
        command.Parameters.Add("@endDate", MySqlDbType.Date).Value = request.EndDate;
        command.Parameters.Add("@isActive", MySqlDbType.Bool).Value = request.IsActive;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("A semester with that name already exists.");
        }
    }

    public async Task<OperationResult> SetSemesterActiveAsync(long semesterId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE semesters SET is_active = @isActive WHERE id = @id;";
        command.Parameters.Add("@isActive", MySqlDbType.Bool).Value = isActive;
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = semesterId;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? OperationResult.Success()
            : OperationResult.Failure("The semester was not found.");
    }

    public async Task<IReadOnlyList<TeacherSemesterEntry>> GetTeacherSemestersAsync(long? semesterId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<TeacherSemesterEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT ts.semester_id, s.name, s.start_date, s.end_date,
                   ts.teacher_account_id, u.display_name, u.employee_number
            FROM teacher_semesters ts
            INNER JOIN semesters s ON s.id = ts.semester_id
            INNER JOIN user_accounts u ON u.id = ts.teacher_account_id
            WHERE (@semesterId IS NULL OR ts.semester_id = @semesterId)
              AND u.role = 'Teacher' AND u.is_active = TRUE
            ORDER BY s.start_date DESC, u.display_name;";
        command.Parameters.AddWithValue("@semesterId", (object?)semesterId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new TeacherSemesterEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetFieldValue<DateOnly>(2),
                reader.GetFieldValue<DateOnly>(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return items;
    }

    public async Task<OperationResult> EnrollTeacherInSemesterAsync(long teacherAccountId, long semesterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO teacher_semesters (teacher_account_id, semester_id)
            SELECT u.id, s.id
            FROM user_accounts u CROSS JOIN semesters s
            WHERE u.id = @teacherId AND u.role = 'Teacher' AND u.is_active = TRUE
              AND s.id = @semesterId;";
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        command.Parameters.Add("@semesterId", MySqlDbType.Int64).Value = semesterId;
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OperationResult.Success()
                : OperationResult.Failure("Select an active teacher and a valid semester.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Success();
        }
    }

    public async Task<OperationResult> UnenrollTeacherFromSemesterAsync(long teacherAccountId, long semesterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT EXISTS(
                SELECT 1 FROM class_schedules
                WHERE teacher_account_id = @teacherId AND semester_id = @semesterId
            );";
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        command.Parameters.Add("@semesterId", MySqlDbType.Int64).Value = semesterId;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
        {
            return OperationResult.Failure("Remove or reassign this teacher's schedules in the semester first.");
        }

        await using var delete = connection.CreateCommand();
        delete.CommandText = @"
            DELETE FROM teacher_semesters
            WHERE teacher_account_id = @teacherId AND semester_id = @semesterId;";
        delete.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        delete.Parameters.Add("@semesterId", MySqlDbType.Int64).Value = semesterId;
        return await delete.ExecuteNonQueryAsync(cancellationToken) == 1
            ? OperationResult.Success()
            : OperationResult.Failure("Enrollment was not found.");
    }

    public Task<IReadOnlyList<ScheduleDirectoryEntry>> GetSchedulesAsync(CancellationToken cancellationToken = default) =>
        ReadSchedulesAsync(null, cancellationToken);

    public Task<IReadOnlyList<ScheduleDirectoryEntry>> GetTeacherSchedulesAsync(long accountId, CancellationToken cancellationToken = default) =>
        ReadSchedulesAsync(accountId, cancellationToken);

    public async Task<OperationResult> MoveScheduleAsync(MoveScheduleRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long teacherId;
            long classroomId;
            long? semesterId;
            await using (var load = connection.CreateCommand())
            {
                load.Transaction = transaction;
                load.CommandText = @"
                    SELECT teacher_account_id, classroom_id, semester_id
                    FROM class_schedules
                    WHERE id = @id
                    FOR UPDATE;";
                load.Parameters.Add("@id", MySqlDbType.Int64).Value = request.ScheduleId;
                await using var reader = await load.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The schedule was not found.");
                }

                teacherId = reader.GetInt64(0);
                classroomId = reader.GetInt64(1);
                semesterId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }

            await using (var active = connection.CreateCommand())
            {
                active.Transaction = transaction;
                active.CommandText = @"
                    SELECT EXISTS(
                        SELECT 1 FROM attendance_logs
                        WHERE class_schedule_id = @id AND status = 'Active'
                    );";
                active.Parameters.Add("@id", MySqlDbType.Int64).Value = request.ScheduleId;
                if (Convert.ToInt64(await active.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("This schedule has an active class session and cannot be moved until it ends.");
                }
            }

            if (semesterId is > 0)
            {
                await using var semester = connection.CreateCommand();
                semester.Transaction = transaction;
                semester.CommandText = @"
                    SELECT start_date, end_date, is_active
                    FROM semesters
                    WHERE id = @semesterId;";
                semester.Parameters.Add("@semesterId", MySqlDbType.Int64).Value = semesterId.Value;
                await using var reader = await semester.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The schedule’s semester was not found.");
                }

                var startDate = reader.GetFieldValue<DateOnly>(0);
                var endDate = reader.GetFieldValue<DateOnly>(1);
                var isActive = reader.GetBoolean(2);
                await reader.DisposeAsync();

                if (!isActive)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The schedule’s semester is inactive.");
                }

                if (request.ScheduleDate < startDate || request.ScheduleDate > endDate)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The new date must fall within the semester date range.");
                }
            }

            await using (var conflict = connection.CreateCommand())
            {
                conflict.Transaction = transaction;
                conflict.CommandText = @"
                    SELECT EXISTS(
                        SELECT 1 FROM class_schedules
                        WHERE id <> @id
                          AND schedule_date = @date
                          AND (teacher_account_id = @teacherId OR classroom_id = @classroomId)
                          AND start_time < @endTime AND end_time > @startTime
                    );";
                conflict.Parameters.Add("@id", MySqlDbType.Int64).Value = request.ScheduleId;
                conflict.Parameters.Add("@date", MySqlDbType.Date).Value = request.ScheduleDate;
                conflict.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherId;
                conflict.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = classroomId;
                conflict.Parameters.Add("@startTime", MySqlDbType.Time).Value = request.StartTime.ToTimeSpan();
                conflict.Parameters.Add("@endTime", MySqlDbType.Time).Value = request.EndTime.ToTimeSpan();
                if (Convert.ToInt64(await conflict.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The selected teacher or classroom already has an overlapping schedule at that time.");
                }
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
                    UPDATE class_schedules
                    SET schedule_date = @date,
                        start_time = @startTime,
                        end_time = @endTime
                    WHERE id = @id;";
                update.Parameters.Add("@id", MySqlDbType.Int64).Value = request.ScheduleId;
                update.Parameters.Add("@date", MySqlDbType.Date).Value = request.ScheduleDate;
                update.Parameters.Add("@startTime", MySqlDbType.Time).Value = request.StartTime.ToTimeSpan();
                update.Parameters.Add("@endTime", MySqlDbType.Time).Value = request.EndTime.ToTimeSpan();
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The schedule could not be moved.");
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<OperationResult> DeleteScheduleAsync(long scheduleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM class_schedules WHERE id = @id;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = scheduleId;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? OperationResult.Success()
            : OperationResult.Failure("The schedule was not found.");
    }

    public async Task<PasswordResetDelivery?> CreatePasswordResetAsync(string email, DateTimeOffset expiresUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long accountId;
            string recipient;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = @"
                    SELECT id, email
                    FROM user_accounts
                    WHERE LOWER(email) = LOWER(@email) AND is_active = TRUE
                    LIMIT 1
                    FOR UPDATE;";
                find.Parameters.Add("@email", MySqlDbType.VarChar).Value = email;
                await using var reader = await find.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }

                accountId = reader.GetInt64(0);
                recipient = reader.GetString(1);
            }

            var rawToken = CreateSecureToken();
            var tokenHash = HashResetToken(rawToken);

            await using (var expireOld = connection.CreateCommand())
            {
                expireOld.Transaction = transaction;
                expireOld.CommandText = @"
                    UPDATE password_reset_tokens
                    SET used_utc = CURRENT_TIMESTAMP
                    WHERE user_account_id = @accountId AND used_utc IS NULL;";
                expireOld.Parameters.Add("@accountId", MySqlDbType.Int64).Value = accountId;
                await expireOld.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"
                    INSERT INTO password_reset_tokens (user_account_id, token_hash, expires_utc)
                    VALUES (@accountId, @tokenHash, @expiresUtc);";
                insert.Parameters.Add("@accountId", MySqlDbType.Int64).Value = accountId;
                insert.Parameters.Add("@tokenHash", MySqlDbType.String).Value = tokenHash;
                insert.Parameters.Add("@expiresUtc", MySqlDbType.DateTime).Value = expiresUtc.UtcDateTime;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new PasswordResetDelivery(recipient, rawToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> ResetPasswordAsync(string rawToken, string passwordHash, CancellationToken cancellationToken = default)
    {
        var tokenHash = HashResetToken(rawToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long accountId;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = @"
                    SELECT user_account_id
                    FROM password_reset_tokens
                    WHERE token_hash = @tokenHash
                      AND used_utc IS NULL
                      AND expires_utc >= CURRENT_TIMESTAMP
                    LIMIT 1
                    FOR UPDATE;";
                find.Parameters.Add("@tokenHash", MySqlDbType.String).Value = tokenHash;
                var account = await find.ExecuteScalarAsync(cancellationToken);
                if (account is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }

                accountId = (long)account;
            }

            await using (var updateAccount = connection.CreateCommand())
            {
                updateAccount.Transaction = transaction;
                updateAccount.CommandText = @"
                    UPDATE user_accounts
                    SET password_hash = @passwordHash
                    WHERE id = @accountId AND is_active = TRUE;";
                updateAccount.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash;
                updateAccount.Parameters.Add("@accountId", MySqlDbType.Int64).Value = accountId;
                if (await updateAccount.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            await using (var useTokens = connection.CreateCommand())
            {
                useTokens.Transaction = transaction;
                useTokens.CommandText = @"
                    UPDATE password_reset_tokens
                    SET used_utc = CURRENT_TIMESTAMP
                    WHERE user_account_id = @accountId AND used_utc IS NULL;";
                useTokens.Parameters.Add("@accountId", MySqlDbType.Int64).Value = accountId;
                await useTokens.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<TeacherProfileEntry?> GetTeacherProfileAsync(long accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT u.id, u.display_name, u.username, u.employee_number, u.email, u.phone, u.device_pin, COUNT(f.id), u.is_active
            FROM user_accounts u LEFT JOIN fingerprint_templates f ON f.user_account_id = u.id
            WHERE u.id = @id AND u.role = 'Teacher'
            GROUP BY u.id, u.display_name, u.username, u.employee_number, u.email, u.phone, u.device_pin, u.is_active;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TeacherProfileEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Convert.ToInt32(reader.GetValue(7)),
                reader.GetBoolean(8))
            : null;
    }

    public async Task<OperationResult> UpdateTeacherProfileAsync(long accountId, UpdateTeacherProfileRequest request, string? passwordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = passwordHash is null
            ? "UPDATE user_accounts SET display_name = @name, email = @email, phone = @phone, device_pin = @devicePin WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;"
            : "UPDATE user_accounts SET display_name = @name, email = @email, phone = @phone, device_pin = @devicePin, password_hash = @passwordHash WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
        command.Parameters.Add("@name", MySqlDbType.VarChar).Value = request.DisplayName;
        command.Parameters.AddWithValue("@email", (object?)request.Email ?? DBNull.Value);
        command.Parameters.Add("@phone", MySqlDbType.VarChar).Value = request.Phone;
        command.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = request.DevicePin!;
        if (passwordHash is not null) command.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash;
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? OperationResult.Success() : OperationResult.Failure("Your teacher account was not found.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("That email, mobile number, or device PIN is already registered to another account.");
        }
    }

    public async Task<IReadOnlyList<AttendanceLogEntry>> GetAttendanceLogsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<AttendanceLogEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT a.id, a.teacher_account_id, teacher.display_name, a.classroom_id, classroom.name,
                   a.time_in_utc, a.time_out_utc, a.verification_method, a.status, a.outcome
            FROM attendance_logs a
            INNER JOIN user_accounts teacher ON teacher.id = a.teacher_account_id
            INNER JOIN classrooms classroom ON classroom.id = a.classroom_id
            WHERE (@teacherId IS NULL OR a.teacher_account_id = @teacherId)
            ORDER BY a.time_in_utc DESC;";
        command.Parameters.AddWithValue("@teacherId", (object?)teacherAccountId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AttendanceLogEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                ReadLocal(reader, 5),
                ReadLocalOrNull(reader, 6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return items;
    }

    public async Task<OperationResult> StartClassAsync(long teacherAccountId, long scheduleId, DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default, string verificationMethod = "Biometric / dashboard verification")
    {
        var lateGraceSeconds = Math.Max(0, _schoolTime.LateGraceMinutes) * 60;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var start = connection.CreateCommand();
            start.Transaction = transaction;
            start.CommandText = @"
                INSERT INTO attendance_logs (class_schedule_id, teacher_account_id, classroom_id, verification_method, outcome, time_in_utc)
                SELECT schedule.id, schedule.teacher_account_id, schedule.classroom_id, @verificationMethod,
                       CASE
                           WHEN @schoolTime > ADDTIME(schedule.start_time, SEC_TO_TIME(@lateGraceSeconds))
                               THEN 'Late'
                           ELSE 'OnTime'
                       END,
                       @recordedAt
                FROM class_schedules schedule
                INNER JOIN semesters semester ON semester.id = schedule.semester_id AND semester.is_active = TRUE
                INNER JOIN teacher_semesters enrollment
                    ON enrollment.semester_id = schedule.semester_id
                   AND enrollment.teacher_account_id = schedule.teacher_account_id
                WHERE schedule.id = @scheduleId AND schedule.teacher_account_id = @teacherId
                                    AND schedule.schedule_date = @schoolDate
                                    AND @schoolTime >= schedule.start_time
                                    AND @schoolTime < schedule.end_time
                                    AND schedule.classroom_id IS NOT NULL
                                    AND @schoolDate BETWEEN semester.start_date AND semester.end_date
                                    AND NOT EXISTS (
                                            SELECT 1
                                            FROM attendance_logs active
                                            WHERE active.teacher_account_id = @teacherId
                                                AND active.time_out_utc IS NULL
                                    )
                                    AND NOT EXISTS (
                                            SELECT 1
                                            FROM attendance_logs existing
                                            WHERE existing.class_schedule_id = schedule.id
                                    );";
            start.Parameters.Add("@scheduleId", MySqlDbType.Int64).Value = scheduleId;
            start.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
            start.Parameters.Add("@schoolDate", MySqlDbType.Date).Value = schoolDate;
            start.Parameters.Add("@schoolTime", MySqlDbType.Time).Value = schoolTime;
            start.Parameters.Add("@verificationMethod", MySqlDbType.VarChar).Value = verificationMethod;
            start.Parameters.Add("@lateGraceSeconds", MySqlDbType.Int32).Value = lateGraceSeconds;
            start.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = SchoolNow();
            if (await start.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("Choose one of your schedules for today that has not already started.");
            }

            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT classroom_id FROM attendance_logs WHERE id = @id;";
            lookup.Parameters.Add("@id", MySqlDbType.Int64).Value = start.LastInsertedId;
            var classroomId = Convert.ToInt64(await lookup.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Attendance log was not created."));

            await SetClassroomStatusAndLogAsync(connection, transaction, classroomId, teacherAccountId, "Cooling", "ClassStarted", null, SchoolNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("This teacher already has an active class.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<OperationResult> EndClassAsync(long teacherAccountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var end = connection.CreateCommand();
            end.Transaction = transaction;
            end.CommandText = @"
                SELECT id, classroom_id FROM attendance_logs
                WHERE teacher_account_id = @teacherId AND time_out_utc IS NULL
                ORDER BY time_in_utc DESC
                LIMIT 1
                FOR UPDATE;";
            end.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
            long attendanceId;
            long classroomId;
            await using (var reader = await end.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("There is no active class attendance record to end.");
                }

                attendanceId = reader.GetInt64(0);
                classroomId = reader.GetInt64(1);
            }

            await using var complete = connection.CreateCommand();
            complete.Transaction = transaction;
            complete.CommandText = @"
                UPDATE attendance_logs
                SET time_out_utc = @recordedAt, status = 'Completed',
                    outcome = COALESCE(outcome, 'OnTime')
                WHERE id = @id;";
            complete.Parameters.Add("@id", MySqlDbType.Int64).Value = attendanceId;
            complete.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = SchoolNow();
            await complete.ExecuteNonQueryAsync(cancellationToken);

            await SetClassroomStatusAndLogAsync(connection, transaction, classroomId, teacherAccountId, "Off", "ClassEnded", null, SchoolNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> ReconcileAttendanceOutcomesAsync(DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default)
    {
        var missingGraceSeconds = Math.Max(0, _schoolTime.MissingTimeOutGraceMinutes) * 60;
        var recordedAt = SchoolNow();
        var changed = 0;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Close open sessions past schedule end + grace → MissingTimeOut; turn AC off.
            await using var findMissing = connection.CreateCommand();
            findMissing.Transaction = transaction;
            findMissing.CommandText = @"
                SELECT a.id, a.classroom_id, a.teacher_account_id
                FROM attendance_logs a
                INNER JOIN class_schedules s ON s.id = a.class_schedule_id
                WHERE a.time_out_utc IS NULL
                  AND a.status = 'Active'
                  AND (
                        s.schedule_date < @schoolDate
                     OR (
                            s.schedule_date = @schoolDate
                        AND ADDTIME(s.end_time, SEC_TO_TIME(@missingGraceSeconds)) <= @schoolTime
                     )
                  )
                FOR UPDATE;";
            findMissing.Parameters.Add("@schoolDate", MySqlDbType.Date).Value = schoolDate;
            findMissing.Parameters.Add("@schoolTime", MySqlDbType.Time).Value = schoolTime;
            findMissing.Parameters.Add("@missingGraceSeconds", MySqlDbType.Int32).Value = missingGraceSeconds;

            var missingSessions = new List<(long AttendanceId, long ClassroomId, long TeacherId)>();
            await using (var reader = await findMissing.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    missingSessions.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
                }
            }

            foreach (var session in missingSessions)
            {
                await using var close = connection.CreateCommand();
                close.Transaction = transaction;
                close.CommandText = @"
                    UPDATE attendance_logs
                    SET time_out_utc = @recordedAt,
                        status = 'Completed',
                        outcome = 'MissingTimeOut'
                    WHERE id = @id AND time_out_utc IS NULL;";
                close.Parameters.Add("@id", MySqlDbType.Int64).Value = session.AttendanceId;
                close.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = recordedAt;
                changed += await close.ExecuteNonQueryAsync(cancellationToken);
                await SetClassroomStatusAndLogAsync(
                    connection,
                    transaction,
                    session.ClassroomId,
                    session.TeacherId,
                    "Off",
                    "ClassEnded",
                    "Auto-closed: missing time-out",
                    recordedAt,
                    cancellationToken);
            }

            // Schedules that ended with no attendance punch → Absent.
            await using var markAbsent = connection.CreateCommand();
            markAbsent.Transaction = transaction;
            markAbsent.CommandText = @"
                INSERT INTO attendance_logs (
                    class_schedule_id, teacher_account_id, classroom_id,
                    time_in_utc, time_out_utc, verification_method, status, outcome)
                SELECT
                    schedule.id,
                    schedule.teacher_account_id,
                    schedule.classroom_id,
                    @recordedAt,
                    @recordedAt,
                    'System',
                    'Completed',
                    'Absent'
                FROM class_schedules schedule
                INNER JOIN semesters semester ON semester.id = schedule.semester_id AND semester.is_active = TRUE
                INNER JOIN teacher_semesters enrollment
                    ON enrollment.semester_id = schedule.semester_id
                   AND enrollment.teacher_account_id = schedule.teacher_account_id
                WHERE schedule.classroom_id IS NOT NULL
                  AND schedule.schedule_date BETWEEN semester.start_date AND semester.end_date
                  AND (
                        schedule.schedule_date < @schoolDate
                     OR (schedule.schedule_date = @schoolDate AND schedule.end_time <= @schoolTime)
                  )
                  AND NOT EXISTS (
                        SELECT 1 FROM attendance_logs existing
                        WHERE existing.class_schedule_id = schedule.id
                  );";
            markAbsent.Parameters.Add("@schoolDate", MySqlDbType.Date).Value = schoolDate;
            markAbsent.Parameters.Add("@schoolTime", MySqlDbType.Time).Value = schoolTime;
            markAbsent.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = recordedAt;
            changed += await markAbsent.ExecuteNonQueryAsync(cancellationToken);

            // Turn Cooling Off as soon as no schedule is still in-window for the room
            // (even if a teacher forgot to log out). Keep Cooling only when another class
            // for that room is currently in its window. Remote / Override stay until explicit Off.
            await using var clearAc = connection.CreateCommand();
            clearAc.Transaction = transaction;
            clearAc.CommandText = @"
                SELECT c.id
                FROM classrooms c
                WHERE c.is_active = TRUE
                  AND c.ac_status = 'Cooling'
                  AND NOT EXISTS (
                        SELECT 1
                        FROM class_schedules s
                        WHERE s.classroom_id = c.id
                          AND s.schedule_date = @schoolDate
                          AND @schoolTime >= s.start_time
                          AND @schoolTime < s.end_time
                  )
                FOR UPDATE;";
            clearAc.Parameters.Add("@schoolDate", MySqlDbType.Date).Value = schoolDate;
            clearAc.Parameters.Add("@schoolTime", MySqlDbType.Time).Value = schoolTime;
            var roomsToClear = new List<long>();
            await using (var reader = await clearAc.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    roomsToClear.Add(reader.GetInt64(0));
                }
            }

            foreach (var classroomId in roomsToClear)
            {
                await SetClassroomStatusAndLogAsync(
                    connection,
                    transaction,
                    classroomId,
                    null,
                    "Off",
                    "ClassEnded",
                    "Auto-off: schedule window ended",
                    recordedAt,
                    cancellationToken);
                changed++;
            }

            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<TeacherRequestEntry>> GetTeacherRequestsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<TeacherRequestEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT request.id, request.teacher_account_id, teacher.display_name, request.request_type,
                   request.request_date, request.reason, request.needs_substitute,
                   request.substitute_teacher_account_id, substitute.display_name, request.status,
                   request.admin_response, request.created_utc, request.reviewed_utc
            FROM teacher_requests request
            INNER JOIN user_accounts teacher ON teacher.id = request.teacher_account_id
            LEFT JOIN user_accounts substitute ON substitute.id = request.substitute_teacher_account_id
            WHERE (@teacherId IS NULL OR request.teacher_account_id = @teacherId)
            ORDER BY request.created_utc DESC;";
        command.Parameters.AddWithValue("@teacherId", (object?)teacherAccountId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new TeacherRequestEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateOnly>(4), reader.GetString(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), ReadLocal(reader, 11), ReadLocalOrNull(reader, 12)));
        }
        return items;
    }

    public async Task<OperationResult> SubmitTeacherRequestAsync(long teacherAccountId, SubmitTeacherRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO teacher_requests (teacher_account_id, request_type, request_date, reason, needs_substitute, created_utc)
            VALUES (@teacherId, @type, @date, @reason, @needsSubstitute, @recordedAt);";
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        command.Parameters.Add("@type", MySqlDbType.VarChar).Value = request.RequestType;
        command.Parameters.Add("@date", MySqlDbType.Date).Value = request.RequestDate;
        command.Parameters.Add("@reason", MySqlDbType.VarChar).Value = request.Reason;
        command.Parameters.Add("@needsSubstitute", MySqlDbType.Bool).Value = request.NeedsSubstitute;
        command.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = SchoolNow();
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("A pending request of this type already exists for that date.");
        }
    }

    public async Task<OperationResult> ReviewTeacherRequestAsync(long adminAccountId, ReviewTeacherRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long requesterId;
            DateOnly requestDate;
            string requestType;
            bool needsSubstitute;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = @"
                    SELECT teacher_account_id, request_date, request_type, needs_substitute
                    FROM teacher_requests WHERE id = @id AND status = 'Pending' FOR UPDATE;";
                read.Parameters.Add("@id", MySqlDbType.Int64).Value = request.RequestId;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The request is no longer pending.");
                }
                requesterId = reader.GetInt64(0);
                requestDate = reader.GetFieldValue<DateOnly>(1);
                requestType = reader.GetString(2);
                needsSubstitute = reader.GetBoolean(3);
            }

            if (requestDate < SchoolClock.Today(_schoolTime))
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("This request can no longer be approved because its date has passed.");
            }

            if (request.Status == "Approved" && requestType == "Leave" && needsSubstitute)
            {
                if (request.SubstituteTeacherAccountId is not long substituteId || substituteId == requesterId)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("Assign a different active teacher as the substitute.");
                }

                await using var substitute = connection.CreateCommand();
                substitute.Transaction = transaction;
                substitute.CommandText = @"
                    SELECT EXISTS(
                        SELECT 1 FROM user_accounts
                        WHERE id = @substituteId AND role = 'Teacher' AND is_active = TRUE
                    );";
                substitute.Parameters.Add("@substituteId", MySqlDbType.Int64).Value = substituteId;
                if (!(Convert.ToInt64(await substitute.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("Assign a different active teacher as the substitute.");
                }

                await using var conflict = connection.CreateCommand();
                conflict.Transaction = transaction;
                conflict.CommandText = @"
                    SELECT EXISTS(
                        SELECT 1
                        FROM class_schedules original
                        INNER JOIN class_schedules substitute
                            ON substitute.teacher_account_id = @substituteId
                           AND substitute.schedule_date = original.schedule_date
                           AND substitute.start_time < original.end_time
                           AND substitute.end_time > original.start_time
                        WHERE original.original_teacher_account_id = @teacherId
                          AND original.schedule_date = @date
                    );";
                conflict.Parameters.Add("@substituteId", MySqlDbType.Int64).Value = substituteId;
                conflict.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = requesterId;
                conflict.Parameters.Add("@date", MySqlDbType.Date).Value = requestDate;
                if (Convert.ToInt64(await conflict.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The selected substitute already has an overlapping schedule.");
                }

                await using var assign = connection.CreateCommand();
                assign.Transaction = transaction;
                assign.CommandText = @"
                    UPDATE class_schedules
                    SET teacher_account_id = @substituteId
                    WHERE original_teacher_account_id = @teacherId AND schedule_date = @date;";
                assign.Parameters.Add("@substituteId", MySqlDbType.Int64).Value = substituteId;
                assign.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = requesterId;
                assign.Parameters.Add("@date", MySqlDbType.Date).Value = requestDate;
                await assign.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
                UPDATE teacher_requests
                SET status = @status, substitute_teacher_account_id = @substituteId,
                    admin_response = @response, reviewed_by_account_id = @adminId, reviewed_utc = @recordedAt
                WHERE id = @id AND status = 'Pending';";
            update.Parameters.Add("@status", MySqlDbType.VarChar).Value = request.Status;
            update.Parameters.AddWithValue("@substituteId", (object?)request.SubstituteTeacherAccountId ?? DBNull.Value);
            update.Parameters.AddWithValue("@response", (object?)request.AdminResponse ?? DBNull.Value);
            update.Parameters.Add("@adminId", MySqlDbType.Int64).Value = adminAccountId;
            update.Parameters.Add("@id", MySqlDbType.Int64).Value = request.RequestId;
            update.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = SchoolNow();
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("The request is no longer pending.");
            }

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<SupportTicketEntry>> GetSupportTicketsAsync(long? teacherAccountId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<SupportTicketEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT ticket.id, ticket.teacher_account_id, teacher.display_name, ticket.category, ticket.details,
                   ticket.status, ticket.admin_response, ticket.created_utc, ticket.resolved_utc
            FROM support_tickets ticket INNER JOIN user_accounts teacher ON teacher.id = ticket.teacher_account_id
            WHERE (@teacherId IS NULL OR ticket.teacher_account_id = @teacherId)
            ORDER BY ticket.created_utc DESC;";
        command.Parameters.AddWithValue("@teacherId", (object?)teacherAccountId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SupportTicketEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), ReadLocal(reader, 7), ReadLocalOrNull(reader, 8)));
        }
        return items;
    }

    public async Task<OperationResult> SubmitSupportTicketAsync(long teacherAccountId, SubmitSupportTicket request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO support_tickets (teacher_account_id, category, details, created_utc)
            VALUES (@teacherId, @category, @details, @recordedAt);";
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        command.Parameters.Add("@category", MySqlDbType.VarChar).Value = request.Category;
        command.Parameters.Add("@details", MySqlDbType.VarChar).Value = request.Details;
        command.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = SchoolNow();
        await command.ExecuteNonQueryAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<TeacherFingerprint>> GetTeacherFingerprintsAsync(long teacherId, CancellationToken cancellationToken = default)
    {
        if (teacherId <= 0) throw new ArgumentOutOfRangeException(nameof(teacherId));
        var fingerprints = new List<TeacherFingerprint>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT f.finger_position, f.template_identifier, d.device_id, b.template_identifier IS NOT NULL
            FROM fingerprint_templates f
            LEFT JOIN fingerprint_template_device_bindings d ON d.fingerprint_template_id = f.id
            LEFT JOIN fingerprint_slot_blocks b ON b.template_identifier = f.template_identifier
            WHERE f.user_account_id = @teacher ORDER BY f.template_identifier;";
        command.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            fingerprints.Add(new TeacherFingerprint(reader.GetString(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3)));
        return fingerprints;
    }

    public async Task<OperationResult> PrepareFingerprintEnrollmentAsync(long teacherId, string fingerPosition, long templateId, CancellationToken cancellationToken = default)
        => await PrepareFingerprintEnrollmentCoreAsync(teacherId, fingerPosition, templateId, cancellationToken);

    public async Task<OperationResult> PrepareFingerprintEnrollmentAutomaticallyAsync(long teacherId, string fingerPosition, CancellationToken cancellationToken = default)
        => await PrepareFingerprintEnrollmentCoreAsync(teacherId, fingerPosition, null, cancellationToken);

    public async Task<OperationResult> PrepareFingerprintReplacementAsync(
        long scannerId, long teacherId, string fingerPosition, long slot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var target = await FingerprintReplacementTarget.LockAndCheckAsync(
            connection, transaction, scannerId, teacherId, fingerPosition, slot, cancellationToken);
        if (!target.Succeeded) return target;

        try
        {
            await using var block = connection.CreateCommand();
            block.Transaction = transaction;
            block.CommandText = "INSERT INTO fingerprint_slot_blocks (template_identifier) VALUES (@slot);";
            block.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            await block.ExecuteNonQueryAsync(cancellationToken);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO fingerprint_enrollment_preparations
                    (id, device_id, teacher_account_id, finger_position, template_identifier, replacement_template_id)
                SELECT @id, @device, f.user_account_id, f.finger_position, f.template_identifier, f.id
                FROM fingerprint_templates f
                WHERE f.user_account_id = @teacher AND f.finger_position = @finger COLLATE utf8mb4_unicode_ci
                  AND f.template_identifier = @slot;";
            insert.Parameters.Add("@id", MySqlDbType.VarChar).Value = Guid.NewGuid().ToString("D");
            insert.Parameters.Add("@device", MySqlDbType.Int64).Value = scannerId;
            insert.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
            insert.Parameters.Add("@finger", MySqlDbType.VarChar).Value = fingerPosition;
            insert.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
                return OperationResult.Failure("The replacement fingerprint no longer exists.");
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("This scanner, slot, or teacher's finger is already reserved or the slot is blocked.");
        }
    }

    private async Task<OperationResult> PrepareFingerprintEnrollmentCoreAsync(long teacherId, string fingerPosition, long? requestedSlot, CancellationToken cancellationToken)
    {
        if (teacherId <= 0 || !FingerprintSlots.IsValid(fingerPosition) ||
            (requestedSlot is not null && (requestedSlot <= 0 || requestedSlot > ushort.MaxValue)))
            return OperationResult.Failure("Select a teacher, a finger, and a sensor slot between 1 and 65535.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var scanner = connection.CreateCommand();
            scanner.Transaction = transaction;
            scanner.CommandText = @"
                SELECT d.id FROM fingerprint_enrollment_scanner e
                INNER JOIN biometric_devices d ON d.id = e.device_id
                INNER JOIN classrooms c ON c.id = d.classroom_id
                WHERE e.singleton_id = 1 AND d.is_active = TRUE AND d.paired_at IS NOT NULL
                  AND d.fingerprint_enrollment_enabled = TRUE AND d.pending_clear_fingerprints = FALSE
                  AND c.is_active = TRUE FOR UPDATE;";
            var deviceId = await scanner.ExecuteScalarAsync(cancellationToken);
            if (deviceId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("Select an active, paired enrollment scanner assigned to an active room, with enrollment enabled and no pending sensor clear.");
            }

            await using var teacher = connection.CreateCommand();
            teacher.Transaction = transaction;
            teacher.CommandText = "SELECT id FROM user_accounts WHERE id = @id AND role = 'Teacher' AND is_active = TRUE FOR UPDATE;";
            teacher.Parameters.Add("@id", MySqlDbType.Int64).Value = teacherId;
            if (await teacher.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("The teacher account was not found or is inactive.");
            }

            await using var fingerprintCount = connection.CreateCommand();
            fingerprintCount.Transaction = transaction;
            fingerprintCount.CommandText = "SELECT COUNT(*) FROM fingerprint_templates WHERE user_account_id = @teacher;";
            fingerprintCount.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
            if (Convert.ToInt64(await fingerprintCount.ExecuteScalarAsync(cancellationToken)) >= 2)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("This teacher already has two or more fingerprints. New enrollment is not allowed.");
            }

            long templateId;
            if (requestedSlot is long specifiedSlot)
            {
                templateId = specifiedSlot;
            }
            else
            {
                await using var inventory = connection.CreateCommand();
                inventory.Transaction = transaction;
                inventory.CommandText = @"SELECT capacity, bitmap FROM fingerprint_sensor_inventory
                    WHERE device_id = @device AND observed_after_utc > DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 90 SECOND)
                    FOR UPDATE;";
                inventory.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceId);
                int capacity;
                byte[] bitmap;
                await using (var reader = await inventory.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                        return OperationResult.Failure("No recent scanner inventory is available. Wait for the scanner to refresh.");
                    capacity = reader.GetInt32(0);
                    bitmap = (byte[])reader.GetValue(1);
                }
                if (capacity is < 2 or > FingerprintSensorInventory.MaximumCapacity || bitmap.Length != (capacity + 7) / 8)
                    return OperationResult.Failure("The scanner inventory is invalid. Wait for the scanner to refresh.");

                await using var unavailable = connection.CreateCommand();
                unavailable.Transaction = transaction;
                unavailable.CommandText = @"SELECT template_identifier FROM fingerprint_templates
                    UNION SELECT template_identifier FROM fingerprint_slot_blocks
                    UNION SELECT template_identifier FROM fingerprint_enrollment_preparations;";
                var reserved = new HashSet<long>();
                await using (var reader = await unavailable.ExecuteReaderAsync(cancellationToken))
                    while (await reader.ReadAsync(cancellationToken)) reserved.Add(reader.GetInt64(0));
                templateId = 0;
                for (var slot = 1; slot < capacity; slot++)
                {
                    if ((bitmap[slot / 8] & (1 << (slot % 8))) == 0 && !reserved.Contains(slot))
                    {
                        templateId = slot;
                        break;
                    }
                }
                if (templateId == 0)
                    return OperationResult.Failure("No available scanner slot was found in the recent inventory.");
            }

            await using var block = connection.CreateCommand();
            block.Transaction = transaction;
            block.CommandText = requestedSlot is null
                ? "INSERT INTO fingerprint_slot_blocks (template_identifier) VALUES (@id);"
                : "INSERT INTO fingerprint_slot_blocks (template_identifier) VALUES (@id) ON DUPLICATE KEY UPDATE template_identifier = VALUES(template_identifier);";
            block.Parameters.Add("@id", MySqlDbType.Int64).Value = templateId;
            await block.ExecuteNonQueryAsync(cancellationToken);

            await using var mapping = connection.CreateCommand();
            mapping.Transaction = transaction;
            mapping.CommandText = @"
                SELECT id FROM fingerprint_templates
                WHERE template_identifier = @slot OR (user_account_id = @teacher AND finger_position = @finger)
                LIMIT 1 FOR UPDATE;";
            mapping.Parameters.Add("@slot", MySqlDbType.Int64).Value = templateId;
            mapping.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
            mapping.Parameters.Add("@finger", MySqlDbType.VarChar).Value = fingerPosition;
            if (await mapping.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("The server slot or this teacher's finger already has a mapping; replacement of mapped fingerprints is not supported yet.");
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO fingerprint_enrollment_preparations
                    (id, device_id, teacher_account_id, finger_position, template_identifier)
                VALUES (@id, @device, @teacher, @finger, @slot);";
            insert.Parameters.Add("@id", MySqlDbType.VarChar).Value = Guid.NewGuid().ToString("D");
            insert.Parameters.Add("@device", MySqlDbType.Int64).Value = Convert.ToInt64(deviceId);
            insert.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
            insert.Parameters.Add("@finger", MySqlDbType.VarChar).Value = fingerPosition;
            insert.Parameters.Add("@slot", MySqlDbType.Int64).Value = templateId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("This scanner, slot, or teacher's finger already has an enrollment preparation.");
        }
    }

    public async Task<IReadOnlyList<FingerprintEnrollmentPreparation>> GetFingerprintEnrollmentPreparationsAsync(CancellationToken cancellationToken = default)
    {
        var preparations = new List<FingerprintEnrollmentPreparation>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT p.id, p.template_identifier, p.finger_position, u.display_name, d.display_name, p.created_utc,
                   g.preparation_id IS NOT NULL,
                   a.expires_utc > UTC_TIMESTAMP(6),
                   a.allow_occupied_slot, p.teacher_account_id, p.replacement_template_id IS NOT NULL, p.device_id
            FROM fingerprint_enrollment_preparations p
            INNER JOIN user_accounts u ON u.id = p.teacher_account_id
            INNER JOIN biometric_devices d ON d.id = p.device_id
            LEFT JOIN fingerprint_enrollment_write_guards g ON g.preparation_id = p.id
            LEFT JOIN fingerprint_enrollment_authorizations a ON a.preparation_id = p.id
            ORDER BY p.created_utc, p.id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            preparations.Add(new FingerprintEnrollmentPreparation(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDateTime(5), reader.GetBoolean(6),
                !reader.IsDBNull(7) && reader.GetBoolean(7), !reader.IsDBNull(8) && reader.GetBoolean(8), reader.GetInt64(9),
                reader.GetBoolean(10), reader.GetInt64(11)));
        return preparations;
    }

    public async Task<OperationResult> AuthorizeFingerprintEnrollmentAsync(long adminAccountId, string id, bool allowOccupiedSlot, CancellationToken cancellationToken = default)
    {
        if (adminAccountId <= 0 || !Guid.TryParseExact(id, "D", out var preparationId) || preparationId == Guid.Empty)
            return OperationResult.Failure("Select a valid preparation and administrator.");
        if (allowOccupiedSlot)
            return OperationResult.Failure("Replacement of an occupied fingerprint slot is not available yet.");
        id = preparationId.ToString("D");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFingerprintDevicesAsync(connection, transaction, cancellationToken);
        await using (var admin = connection.CreateCommand())
        {
            admin.Transaction = transaction;
            admin.CommandText = "SELECT id FROM user_accounts WHERE id = @id AND role = 'Admin' AND is_active = TRUE FOR UPDATE;";
            admin.Parameters.Add("@id", MySqlDbType.Int64).Value = adminAccountId;
            if (await admin.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The administrator account is no longer active.");
        }
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = @"
                SELECT p.id FROM fingerprint_enrollment_preparations p
                INNER JOIN biometric_devices d ON d.id = p.device_id
                INNER JOIN fingerprint_enrollment_scanner s ON s.singleton_id = 1 AND s.device_id = d.id
                INNER JOIN classrooms c ON c.id = d.classroom_id
                INNER JOIN user_accounts u ON u.id = p.teacher_account_id
                INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
                WHERE p.id = @id AND p.replacement_template_id IS NULL AND d.is_active = TRUE AND d.paired_at IS NOT NULL
                  AND d.fingerprint_enrollment_enabled = TRUE AND d.pending_clear_fingerprints = FALSE
                  AND c.is_active = TRUE AND u.is_active = TRUE AND u.role = 'Teacher'
                  AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_write_guards g WHERE g.preparation_id = p.id)
                  AND NOT EXISTS (SELECT 1 FROM fingerprint_templates f WHERE f.template_identifier = p.template_identifier
                                  OR (f.user_account_id = p.teacher_account_id AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci))
                FOR UPDATE;";
            check.Parameters.Add("@id", MySqlDbType.VarChar).Value = id;
            if (await check.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The preparation is no longer eligible, has a mapping, or has an unresolved write.");
        }
        var saved = await SaveFingerprintAuthorizationAsync(connection, transaction, adminAccountId, id, false, cancellationToken);
        if (!saved.Succeeded) return saved;
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    // Internal consent prerequisite only; normal operation and claim paths still reject replacement.
    public async Task<OperationResult> AuthorizeFingerprintReplacementAsync(
        long adminAccountId, long scannerId, long teacherId, string fingerPosition, long slot, string id,
        CancellationToken cancellationToken = default)
    {
        if (adminAccountId <= 0 || !Guid.TryParseExact(id, "D", out var preparationId) || preparationId == Guid.Empty)
            return OperationResult.Failure("Select a valid preparation and administrator.");
        id = preparationId.ToString("D");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFingerprintDevicesAsync(connection, transaction, cancellationToken);
        await using (var admin = connection.CreateCommand())
        {
            admin.Transaction = transaction;
            admin.CommandText = "SELECT id FROM user_accounts WHERE id = @id AND role = 'Admin' AND is_active = TRUE FOR UPDATE;";
            admin.Parameters.Add("@id", MySqlDbType.Int64).Value = adminAccountId;
            if (await admin.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The administrator account is no longer active.");
        }
        var target = await FingerprintReplacementTarget.LockAndCheckAsync(
            connection, transaction, scannerId, teacherId, fingerPosition, slot, cancellationToken);
        if (!target.Succeeded) return target;

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = @"
                SELECT p.id FROM fingerprint_enrollment_preparations p
                INNER JOIN fingerprint_slot_blocks b ON b.template_identifier = p.template_identifier
                INNER JOIN fingerprint_templates f ON f.id = p.replacement_template_id
                  AND f.user_account_id = p.teacher_account_id
                  AND f.finger_position = p.finger_position COLLATE utf8mb4_unicode_ci
                  AND f.template_identifier = p.template_identifier
                WHERE p.id = @id AND p.device_id = @scanner AND p.teacher_account_id = @teacher
                  AND p.finger_position = @finger COLLATE utf8mb4_unicode_ci AND p.template_identifier = @slot
                  AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_write_guards g WHERE g.preparation_id = p.id)
                FOR UPDATE;";
            check.Parameters.Add("@id", MySqlDbType.VarChar).Value = id;
            check.Parameters.Add("@scanner", MySqlDbType.Int64).Value = scannerId;
            check.Parameters.Add("@teacher", MySqlDbType.Int64).Value = teacherId;
            check.Parameters.Add("@finger", MySqlDbType.VarChar).Value = fingerPosition;
            check.Parameters.Add("@slot", MySqlDbType.Int64).Value = slot;
            if (await check.ExecuteScalarAsync(cancellationToken) is null)
                return OperationResult.Failure("The replacement preparation no longer matches or has an unresolved write.");
        }
        var saved = await SaveFingerprintAuthorizationAsync(connection, transaction, adminAccountId, id, true, cancellationToken);
        if (!saved.Succeeded) return saved;
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static async Task<OperationResult> SaveFingerprintAuthorizationAsync(
        MySqlConnection connection, MySqlTransaction transaction, long adminAccountId, string id,
        bool allowOccupiedSlot, CancellationToken cancellationToken)
    {
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT expires_utc > UTC_TIMESTAMP(6) FROM fingerprint_enrollment_authorizations WHERE preparation_id = @id FOR UPDATE;";
            existing.Parameters.Add("@id", MySqlDbType.VarChar).Value = id;
            var active = await existing.ExecuteScalarAsync(cancellationToken);
            if (active is not null && Convert.ToBoolean(active))
                return OperationResult.Failure("This preparation already has active authorization. Revoke it before changing consent.");
        }
        await using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = @"
                INSERT INTO fingerprint_enrollment_authorizations
                    (preparation_id, operation_id, authorized_by, allow_occupied_slot, created_utc, expires_utc)
                VALUES (@id, @operation, @admin, @occupied, UTC_TIMESTAMP(6), DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 5 MINUTE))
                ON DUPLICATE KEY UPDATE operation_id = VALUES(operation_id), authorized_by = VALUES(authorized_by),
                    allow_occupied_slot = VALUES(allow_occupied_slot), created_utc = VALUES(created_utc), expires_utc = VALUES(expires_utc);";
            save.Parameters.Add("@id", MySqlDbType.VarChar).Value = id;
            save.Parameters.Add("@operation", MySqlDbType.VarChar).Value = Guid.NewGuid().ToString("D");
            save.Parameters.Add("@admin", MySqlDbType.Int64).Value = adminAccountId;
            save.Parameters.Add("@occupied", MySqlDbType.Bool).Value = allowOccupiedSlot;
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        return OperationResult.Success();
    }

    public async Task<OperationResult> RevokeFingerprintEnrollmentAuthorizationAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(id, "D", out var preparationId) || preparationId == Guid.Empty)
            return OperationResult.Failure("Select a valid preparation.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFingerprintDevicesAsync(connection, transaction, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"DELETE a FROM fingerprint_enrollment_authorizations a
            WHERE a.preparation_id = @id
              AND NOT EXISTS (SELECT 1 FROM fingerprint_enrollment_write_guards g WHERE g.preparation_id = a.preparation_id);";
        command.Parameters.Add("@id", MySqlDbType.VarChar).Value = preparationId.ToString("D");
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            return OperationResult.Failure("Authorization was not found or the sensor write is unresolved.");
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CancelFingerprintEnrollmentPreparationAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(id, "D", out _))
            return OperationResult.Failure("Select a valid preparation.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM fingerprint_enrollment_preparations WHERE id = @id;";
        command.Parameters.Add("@id", MySqlDbType.VarChar).Value = id;
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OperationResult.Success()
                : OperationResult.Failure("The enrollment preparation no longer exists.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.RowIsReferenced2)
        {
            return OperationResult.Failure("This preparation has an unresolved sensor write and cannot be cancelled.");
        }
    }

    public async Task<OperationResult> ReviewSupportTicketAsync(long adminAccountId, ReviewSupportTicket request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE support_tickets
            SET status = @status, admin_response = @response, reviewed_by_account_id = @adminId, resolved_utc = @recordedAt
            WHERE id = @id AND status = 'Open';";
        command.Parameters.Add("@status", MySqlDbType.VarChar).Value = request.Status;
        command.Parameters.AddWithValue("@response", (object?)request.AdminResponse ?? DBNull.Value);
        command.Parameters.Add("@adminId", MySqlDbType.Int64).Value = adminAccountId;
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = request.TicketId;
        command.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = SchoolNow();
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? OperationResult.Success()
            : OperationResult.Failure("The ticket is no longer open.");
    }

    public async Task<IReadOnlyList<TemperatureLogEntry>> GetTemperatureLogsAsync(long? classroomId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<TemperatureLogEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT log.id, log.classroom_id, classroom.name, log.measured_temperature, log.target_temperature,
                   log.event_type, log.notes, log.recorded_utc
            FROM temperature_logs log INNER JOIN classrooms classroom ON classroom.id = log.classroom_id
            WHERE (@classroomId IS NULL OR log.classroom_id = @classroomId)
            ORDER BY log.recorded_utc DESC
            LIMIT 100;";
        command.Parameters.AddWithValue("@classroomId", (object?)classroomId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new TemperatureLogEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3), reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), ReadLocal(reader, 7)));
        }
        return items;
    }

    public async Task<OperationResult> SetTemperatureAsync(long actorAccountId, SetTemperatureRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var actor = connection.CreateCommand();
            actor.Transaction = transaction;
            actor.CommandText = "SELECT role, is_active FROM user_accounts WHERE id = @actor FOR UPDATE;";
            actor.Parameters.Add("@actor", MySqlDbType.Int64).Value = actorAccountId;
            string? role = null;
            await using (var reader = await actor.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken) && reader.GetBoolean(1))
                    role = reader.GetString(0);
            }
            if (role is not ("Admin" or "Teacher"))
                return OperationResult.Failure("An active account is required.");
            if (role == "Teacher")
            {
                await using var attendance = connection.CreateCommand();
                attendance.Transaction = transaction;
                attendance.CommandText = """
                    SELECT id FROM attendance_logs
                    WHERE teacher_account_id = @teacher AND classroom_id = @room
                      AND status = 'Active' AND time_out_utc IS NULL LIMIT 1 FOR UPDATE;
                    """;
                attendance.Parameters.Add("@teacher", MySqlDbType.Int64).Value = actorAccountId;
                attendance.Parameters.Add("@room", MySqlDbType.Int64).Value = request.ClassroomId;
                if (await attendance.ExecuteScalarAsync(cancellationToken) is null)
                    return OperationResult.Failure("Only the teacher with an active class in this room can adjust its temperature.");

                await using var room = connection.CreateCommand();
                room.Transaction = transaction;
                room.CommandText = "SELECT minimum_temperature FROM classrooms WHERE id = @room AND is_active = TRUE FOR UPDATE;";
                room.Parameters.Add("@room", MySqlDbType.Int64).Value = request.ClassroomId;
                var minimum = await room.ExecuteScalarAsync(cancellationToken);
                if (minimum is null) return OperationResult.Failure("The active classroom was not found.");
                if (request.TargetTemperature < Convert.ToInt32(minimum))
                    return OperationResult.Failure($"The classroom minimum is {Convert.ToInt32(minimum)}°C.");
            }
            var status = request.EventType switch
            {
                "HardShutdown" or "RemoteOff" => "Off",
                "EmergencyOverride" => "Override",
                "RemoteOn" => "Remote",
                _ => "Cooling"
            };
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
                UPDATE classrooms
                SET target_temperature = @targetTemperature, ac_status = @status
                WHERE id = @classroomId AND is_active = TRUE;";
            update.Parameters.Add("@targetTemperature", MySqlDbType.Int32).Value = request.TargetTemperature;
            update.Parameters.Add("@status", MySqlDbType.VarChar).Value = status;
            update.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = request.ClassroomId;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("The active classroom was not found.");
            }

            await InsertTemperatureLogAsync(connection, transaction, request.ClassroomId, actorAccountId, request.EventType, request.TargetTemperature, request.Notes, SchoolNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<OperationResult> ResetSystemToFirstAccessAsync(DefaultAdminOptions defaultAdmin, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await HasUnresolvedFingerprintWritesAsync(connection, transaction, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("Resolve outstanding fingerprint writes before resetting the system.");
            }
            await LockFingerprintDevicesAsync(connection, transaction, cancellationToken);
            await using (var pendingIr = connection.CreateCommand())
            {
                pendingIr.Transaction = transaction;
                pendingIr.CommandText = "SELECT device_id FROM ac_ir_state WHERE state = 'inflight' LIMIT 1 FOR UPDATE;";
                if (await pendingIr.ExecuteScalarAsync(cancellationToken) is not null)
                    return OperationResult.Failure("Resolve outstanding IR commands before resetting the system.");
            }
            foreach (var table in new[]
            {
                "temperature_logs", "attendance_logs", "teacher_requests", "support_tickets",
                "class_schedules", "teacher_semesters", "semesters", "biometric_devices", "classrooms",
                "password_reset_tokens", "fingerprint_templates", "user_accounts"
            })
            {
                await using var wipe = connection.CreateCommand();
                wipe.Transaction = transaction;
                wipe.CommandText = $"DELETE FROM {table};";
                await wipe.ExecuteNonQueryAsync(cancellationToken);
            }

            await SeedDefaultAdminAsync(connection, defaultAdmin, passwordHash, cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<IReadOnlyList<ScheduleDirectoryEntry>> ReadSchedulesAsync(long? teacherAccountId, CancellationToken cancellationToken)
    {
        var items = new List<ScheduleDirectoryEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT schedule.id, schedule.teacher_account_id, teacher.display_name,
                   COALESCE(schedule.original_teacher_account_id, schedule.teacher_account_id), original_teacher.display_name,
                   classroom.id, classroom.name, classroom.capacity,
                   schedule.semester_id, semester.name, schedule.schedule_kind,
                   schedule.subject_name, schedule.schedule_date, schedule.start_time, schedule.end_time
            FROM class_schedules schedule
            INNER JOIN user_accounts teacher ON teacher.id = schedule.teacher_account_id
            INNER JOIN user_accounts original_teacher ON original_teacher.id = COALESCE(schedule.original_teacher_account_id, schedule.teacher_account_id)
            INNER JOIN classrooms classroom ON classroom.id = schedule.classroom_id
            LEFT JOIN semesters semester ON semester.id = schedule.semester_id
            WHERE (@teacherId IS NULL OR schedule.teacher_account_id = @teacherId)
            ORDER BY schedule.schedule_date, schedule.start_time, classroom.name;";
        command.Parameters.AddWithValue("@teacherId", (object?)teacherAccountId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ScheduleDirectoryEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? ScheduleKinds.Regular : reader.GetString(10),
                reader.GetString(11),
                reader.GetFieldValue<DateOnly>(12),
                reader.GetFieldValue<TimeOnly>(13),
                reader.GetFieldValue<TimeOnly>(14)));
        }
        return items;
    }

    private static void AddClassroomParameters(MySqlCommand command, string name, int capacity, int targetTemperature)
    {
        command.Parameters.Add("@name", MySqlDbType.VarChar).Value = name;
        command.Parameters.Add("@capacity", MySqlDbType.Int32).Value = capacity;
        command.Parameters.Add("@targetTemperature", MySqlDbType.Int32).Value = targetTemperature;
    }

    private static void AddScheduleParameters(MySqlCommand command, CreateScheduleRequest request)
    {
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = request.TeacherAccountId;
        command.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = request.ClassroomId;
        command.Parameters.Add("@semesterId", MySqlDbType.Int64).Value = request.SemesterId;
        command.Parameters.Add("@scheduleKind", MySqlDbType.VarChar).Value = request.ScheduleKind;
        command.Parameters.Add("@subject", MySqlDbType.VarChar).Value = request.SubjectName;
        command.Parameters.Add("@date", MySqlDbType.Date).Value = request.ScheduleDate;
        command.Parameters.Add("@startTime", MySqlDbType.Time).Value = request.StartTime;
        command.Parameters.Add("@endTime", MySqlDbType.Time).Value = request.EndTime;
    }

    private static async Task SetClassroomStatusAndLogAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long classroomId,
        long? actorAccountId,
        string status,
        string eventType,
        string? notes,
        DateTime recordedAt,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE classrooms SET ac_status = @status WHERE id = @id;";
        update.Parameters.Add("@status", MySqlDbType.VarChar).Value = status;
        update.Parameters.Add("@id", MySqlDbType.Int64).Value = classroomId;
        await update.ExecuteNonQueryAsync(cancellationToken);
        await InsertTemperatureLogAsync(connection, transaction, classroomId, actorAccountId, eventType, null, notes, recordedAt, cancellationToken);
    }

    private static async Task InsertTemperatureLogAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long classroomId,
        long? actorAccountId,
        string eventType,
        int? targetTemperature,
        string? notes,
        DateTime recordedAt,
        CancellationToken cancellationToken)
    {
        await using var log = connection.CreateCommand();
        log.Transaction = transaction;
        log.CommandText = @"
            INSERT INTO temperature_logs (classroom_id, actor_account_id, target_temperature, event_type, notes, recorded_utc)
            VALUES (@classroomId, @actorId, @targetTemperature, @eventType, @notes, @recordedAt);";
        log.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = classroomId;
        log.Parameters.AddWithValue("@actorId", (object?)actorAccountId ?? DBNull.Value);
        log.Parameters.AddWithValue("@targetTemperature", (object?)targetTemperature ?? DBNull.Value);
        log.Parameters.Add("@eventType", MySqlDbType.VarChar).Value = eventType;
        log.Parameters.AddWithValue("@notes", (object?)notes ?? DBNull.Value);
        log.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = recordedAt;
        await log.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateSecureToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string HashResetToken(string rawToken) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    private DateTime SchoolNow() => SchoolClock.GetNow(_schoolTime);

    private static DateTime ReadLocal(MySqlDataReader reader, int ordinal) =>
        DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Unspecified);

    private static DateTime? ReadLocalOrNull(MySqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadLocal(reader, ordinal);

    private async Task<MySqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task SeedDefaultAdminAsync(MySqlConnection connection, DefaultAdminOptions admin, string passwordHash, CancellationToken cancellationToken, MySqlTransaction? transaction = null)
    {
        await using var seed = connection.CreateCommand();
        seed.Transaction = transaction;
        seed.CommandText = @"
            INSERT INTO user_accounts (username, display_name, password_hash, role, is_default_admin)
            SELECT @username, 'System Administrator', @passwordHash, 'Admin', TRUE
            FROM DUAL
            WHERE NOT EXISTS (SELECT 1 FROM user_accounts WHERE role = 'Admin');";
        seed.Parameters.AddWithValue("@username", admin.Username.Trim());
        seed.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash;
        await seed.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OperationResult> EnrollFingerprintAsync(long teacherAccountId, string fingerPosition, long templateId, CancellationToken cancellationToken = default)
    {
        if (templateId <= 0)
        {
            return OperationResult.Failure("templateId must be a positive number.");
        }

        if (!FingerprintSlots.IsValid(fingerPosition))
        {
            return OperationResult.Failure("fingerPosition must be one of the ten supported finger slots.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using (var teacher = connection.CreateCommand())
        {
            teacher.CommandText = @"
                SELECT EXISTS(
                    SELECT 1 FROM user_accounts
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE
                );";
            teacher.Parameters.Add("@id", MySqlDbType.Int64).Value = teacherAccountId;
            if (Convert.ToInt64(await teacher.ExecuteScalarAsync(cancellationToken) ?? 0L) == 0)
            {
                return OperationResult.Failure("The teacher account was not found or is inactive.");
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await IsFingerprintSlotBlockedForUpdateAsync(connection, transaction, templateId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("That sensor slot is blocked and cannot be enrolled.");
            }

            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = @"
                SELECT id FROM fingerprint_templates
                WHERE user_account_id = @teacherId AND finger_position = @fingerPosition
                FOR UPDATE;";
            existing.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
            existing.Parameters.Add("@fingerPosition", MySqlDbType.VarChar).Value = fingerPosition;
            var existingId = await existing.ExecuteScalarAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = existingId is null
                ? @"INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                    VALUES (@teacherId, @fingerPosition, @templateId);"
                : "UPDATE fingerprint_templates SET template_identifier = @templateId WHERE id = @id;";
            command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
            command.Parameters.Add("@fingerPosition", MySqlDbType.VarChar).Value = fingerPosition;
            command.Parameters.Add("@templateId", MySqlDbType.Int64).Value = templateId;
            if (existingId is not null)
            {
                command.Parameters.Add("@id", MySqlDbType.Int64).Value = Convert.ToInt64(existingId);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("That fingerprint template ID is already enrolled to another teacher or finger.");
        }
    }

    public async Task<TeacherProfileEntry?> FindTeacherProfileByPinAsync(string pinCode, CancellationToken cancellationToken = default)
    {
        if (!DevicePinCodes.IsValid(pinCode))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT u.id
            FROM user_accounts u
            WHERE u.device_pin = @devicePin
              AND u.role = 'Teacher'
              AND u.is_active = TRUE
            LIMIT 1;";
        command.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = DevicePinCodes.Normalize(pinCode);
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return id is null or DBNull
            ? null
            : await GetTeacherProfileAsync(Convert.ToInt64(id), cancellationToken);
    }

    public async Task<DeviceFingerprintEnrollResult> EnrollFingerprintByPinAsync(string pinCode, long templateId, CancellationToken cancellationToken = default)
    {
        if (!DevicePinCodes.IsValid(pinCode))
        {
            return new DeviceFingerprintEnrollResult(false, "pinCode must be 4–6 digits.");
        }

        if (templateId <= 0)
        {
            return new DeviceFingerprintEnrollResult(false, "templateId must be a positive number.");
        }

        var profile = await FindTeacherProfileByPinAsync(pinCode, cancellationToken);
        if (profile is null)
        {
            return new DeviceFingerprintEnrollResult(false, "No active teacher matched that PIN.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await IsFingerprintSlotBlockedForUpdateAsync(connection, transaction, templateId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DeviceFingerprintEnrollResult(false, "That sensor slot is blocked and cannot be enrolled.");
            }

            var usedSlots = new HashSet<string>(StringComparer.Ordinal);
            await using (var slots = connection.CreateCommand())
            {
                slots.Transaction = transaction;
                slots.CommandText = @"
                    SELECT finger_position
                    FROM fingerprint_templates
                    WHERE user_account_id = @teacherId
                    FOR UPDATE;";
                slots.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = profile.AccountId;
                await using var reader = await slots.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    usedSlots.Add(reader.GetString(0));
                }
            }

            var fingerPosition = FingerprintSlots.All.FirstOrDefault(slot => !usedSlots.Contains(slot));
            if (fingerPosition is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DeviceFingerprintEnrollResult(
                    Ok: false,
                    Error: "All ten fingerprint slots are already enrolled for this teacher.",
                    TeacherAccountId: profile.AccountId,
                    DisplayName: profile.DisplayName,
                    Username: profile.Username,
                    EmployeeNumber: profile.EmployeeNumber,
                    DevicePin: profile.DevicePin,
                    Email: profile.Email,
                    Phone: profile.Phone,
                    FingerprintTemplateCount: usedSlots.Count);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"
                    INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
                    VALUES (@teacherId, @fingerPosition, @templateId);";
                insert.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = profile.AccountId;
                insert.Parameters.Add("@fingerPosition", MySqlDbType.VarChar).Value = fingerPosition;
                insert.Parameters.Add("@templateId", MySqlDbType.Int64).Value = templateId;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new DeviceFingerprintEnrollResult(
                Ok: true,
                TeacherAccountId: profile.AccountId,
                DisplayName: profile.DisplayName,
                Username: profile.Username,
                EmployeeNumber: profile.EmployeeNumber,
                DevicePin: profile.DevicePin,
                Email: profile.Email,
                Phone: profile.Phone,
                FingerprintTemplateCount: usedSlots.Count + 1,
                TemplateId: templateId,
                FingerPosition: fingerPosition);
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeviceFingerprintEnrollResult(false, "That sensor slot already has a fingerprint mapping. Explicit replacement is not yet available.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<string> ResolveNewTeacherPinAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string? requestedPin,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedPin))
        {
            var normalized = DevicePinCodes.Normalize(requestedPin);
            if (!DevicePinCodes.IsValid(normalized))
            {
                throw new InvalidOperationException(DevicePinCodes.RequirementMessage);
            }

            if (await DevicePinExistsAsync(connection, transaction, normalized, excludeAccountId: null, cancellationToken))
            {
                throw new InvalidOperationException("That device PIN is already registered.");
            }

            return normalized;
        }

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var candidate = DevicePinCodes.Generate();
            if (!await DevicePinExistsAsync(connection, transaction, candidate, excludeAccountId: null, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique device PIN.");
    }

    private static async Task<bool> DevicePinExistsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string pin,
        long? excludeAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = excludeAccountId is null
            ? "SELECT EXISTS(SELECT 1 FROM user_accounts WHERE device_pin = @pin);"
            : "SELECT EXISTS(SELECT 1 FROM user_accounts WHERE device_pin = @pin AND id <> @id);";
        command.Parameters.Add("@pin", MySqlDbType.VarChar).Value = pin;
        if (excludeAccountId is not null)
        {
            command.Parameters.Add("@id", MySqlDbType.Int64).Value = excludeAccountId.Value;
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0;
    }

    public async Task<FingerprintTeacherMatch?> FindTeacherByFingerprintTemplateIdAsync(long templateId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT u.id, u.display_name, u.username
            FROM fingerprint_templates f
            INNER JOIN user_accounts u ON u.id = f.user_account_id
            WHERE f.template_identifier = @templateId
              AND NOT EXISTS (
                  SELECT 1 FROM fingerprint_slot_blocks b
                  WHERE b.template_identifier = f.template_identifier
              )
              AND (
                  NOT EXISTS (SELECT 1 FROM fingerprint_template_device_bindings b WHERE b.fingerprint_template_id = f.id)
                  OR EXISTS (SELECT 1 FROM fingerprint_template_device_bindings b
                             WHERE b.fingerprint_template_id = f.id AND b.device_id = @deviceId)
              )
              AND u.role = 'Teacher'
              AND u.is_active = TRUE
            LIMIT 1;";
        command.Parameters.Add("@templateId", MySqlDbType.Int64).Value = templateId;
        command.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FingerprintTeacherMatch(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<IReadOnlyList<long>> GetBlockedFingerprintSlotsAsync(CancellationToken cancellationToken = default)
    {
        var slots = new List<long>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT template_identifier FROM fingerprint_slot_blocks ORDER BY template_identifier;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            slots.Add(reader.GetInt64(0));
        }
        return slots;
    }

    public async Task<FingerprintSlotInspection> InspectFingerprintSlotAsync(long templateId, CancellationToken cancellationToken = default)
    {
        if (templateId <= 0 || templateId > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(templateId));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT EXISTS(SELECT 1 FROM fingerprint_slot_blocks WHERE template_identifier = @id),
                   u.display_name, u.is_active
            FROM (SELECT @id AS slot_id) AS requested
            LEFT JOIN fingerprint_templates f ON f.template_identifier = requested.slot_id
            LEFT JOIN user_accounts u ON u.id = f.user_account_id;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = templateId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Fingerprint slot inspection returned no result.");
        }
        return new FingerprintSlotInspection(
            templateId,
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetBoolean(2));
    }

    public async Task<OperationResult> SetFingerprintSlotBlockedAsync(long templateId, bool blocked, CancellationToken cancellationToken = default)
    {
        if (templateId <= 0 || templateId > ushort.MaxValue)
        {
            return OperationResult.Failure("Enter a sensor slot between 1 and 65535.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = blocked
            ? "INSERT INTO fingerprint_slot_blocks (template_identifier) VALUES (@id) ON DUPLICATE KEY UPDATE template_identifier = VALUES(template_identifier);"
            : "DELETE FROM fingerprint_slot_blocks WHERE template_identifier = @id;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = templateId;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (!blocked && exception.ErrorCode == MySqlErrorCode.RowIsReferenced2)
        {
            return OperationResult.Failure("Cancel the enrollment preparation before removing its slot block.");
        }
        return OperationResult.Success();
    }

    private static async Task<bool> IsFingerprintSlotBlockedForUpdateAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long templateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT template_identifier FROM fingerprint_slot_blocks WHERE template_identifier = @id FOR UPDATE;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = templateId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<OperationResult> SetAdminDevicePinHashAsync(long adminAccountId, string pinHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE user_accounts
            SET admin_device_pin_hash = @hash
            WHERE id = @id AND role = 'Admin' AND is_active = TRUE;";
        command.Parameters.Add("@hash", MySqlDbType.VarChar).Value = pinHash;
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = adminAccountId;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? OperationResult.Success()
            : OperationResult.Failure("Administrator account was not found.");
    }

    public async Task<IReadOnlyList<string>> GetAdminDevicePinHashesAsync(CancellationToken cancellationToken = default)
    {
        var hashes = new List<string>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT admin_device_pin_hash FROM user_accounts
            WHERE role = 'Admin' AND is_active = TRUE AND admin_device_pin_hash IS NOT NULL;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
    }

    public async Task<BiometricDeviceEntry?> FindBiometricDeviceByIdAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT d.id, d.classroom_id, c.name, d.device_id, d.display_name, d.is_active,
                   c.target_temperature, d.status_poll_interval_seconds,
                   d.fingerprint_enrollment_enabled, d.attendance_scan_enabled
            FROM biometric_devices d
            LEFT JOIN classrooms c ON c.id = d.classroom_id
            WHERE UPPER(d.device_id) = @deviceId
              AND d.is_active = TRUE
              AND d.paired_at IS NOT NULL
              AND (d.classroom_id IS NULL OR c.is_active = TRUE)
            LIMIT 1;";
        command.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId.Trim().ToUpperInvariant();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BiometricDeviceEntry(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9));
    }

    public async Task<IReadOnlyList<BiometricDeviceEntry>> GetBiometricDevicesAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<BiometricDeviceEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT d.id, d.classroom_id, c.name, d.device_id, d.display_name, d.is_active,
                   c.target_temperature, d.status_poll_interval_seconds,
                   d.fingerprint_enrollment_enabled, d.attendance_scan_enabled,
                   d.pairing_code, d.paired_at IS NOT NULL, e.device_id IS NOT NULL
            FROM biometric_devices d
            LEFT JOIN classrooms c ON c.id = d.classroom_id
            LEFT JOIN fingerprint_enrollment_scanner e ON e.device_id = d.id
            ORDER BY d.id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BiometricDeviceEntry(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12)));
        }
        return items;
    }

    public async Task<OperationResult> SetEnrollmentScannerAsync(long? deviceRowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var hasUnresolvedWrite = await HasUnresolvedFingerprintWritesAsync(connection, transaction, cancellationToken);
        await using var current = connection.CreateCommand();
        current.Transaction = transaction;
        current.CommandText = "SELECT device_id FROM fingerprint_enrollment_scanner WHERE singleton_id = 1 FOR UPDATE;";
        var selected = await current.ExecuteScalarAsync(cancellationToken);
        long? selectedId = selected is null ? null : Convert.ToInt64(selected);
        if (selectedId == deviceRowId)
        {
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        if (hasUnresolvedWrite)
            return OperationResult.Failure("The enrollment scanner cannot be changed while a fingerprint write is unresolved.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (deviceRowId is null)
        {
            command.CommandText = "DELETE FROM fingerprint_enrollment_scanner WHERE singleton_id = 1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }

        command.CommandText = @"
            INSERT INTO fingerprint_enrollment_scanner (singleton_id, device_id)
            SELECT 1, id FROM biometric_devices WHERE id = @id
            ON DUPLICATE KEY UPDATE device_id = VALUES(device_id);";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceRowId.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "SELECT 1 FROM fingerprint_enrollment_scanner WHERE singleton_id = 1 AND device_id = @id;";
        verify.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceRowId.Value;
        if (await verify.ExecuteScalarAsync(cancellationToken) is null)
            return OperationResult.Failure("The selected scanner no longer exists or the selection changed. Try again.");
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static async Task AssignNewPairingCodeAsync(MySqlConnection connection, MySqlTransaction? transaction, long deviceRowId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE biometric_devices SET pairing_code = @code WHERE id = @id;";
            command.Parameters.Add("@code", MySqlDbType.VarChar).Value = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            command.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceRowId;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                // Another pending device uses this code; draw a new one.
            }
        }

        throw new InvalidOperationException("Could not generate a unique pairing code.");
    }

    public async Task<OperationResult> RegenerateDevicePairingCodeAsync(long deviceRowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM biometric_devices WHERE id = @id;";
        exists.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceRowId;
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken) ?? 0L) == 0)
        {
            return OperationResult.Failure("Device was not found.");
        }

        await AssignNewPairingCodeAsync(connection, null, deviceRowId, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<BiometricDeviceEntry?> PairDeviceByCodeAsync(string pairingCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT device_id FROM biometric_devices WHERE pairing_code = @code AND is_active = TRUE LIMIT 1 FOR UPDATE;";
        find.Parameters.Add("@code", MySqlDbType.VarChar).Value = pairingCode;
        var deviceId = await find.ExecuteScalarAsync(cancellationToken) as string;
        if (deviceId is null)
        {
            return null;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            SELECT d.id FROM biometric_devices d
            INNER JOIN ac_ir_state s ON s.device_id = d.id AND s.state = 'inflight'
            WHERE d.device_id = @deviceId FOR UPDATE;
            """;
        update.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId;
        if (await update.ExecuteScalarAsync(cancellationToken) is not null) return null;
        update.CommandText = @"
            UPDATE biometric_devices
            SET paired_at = UTC_TIMESTAMP(), pairing_code = NULL
            WHERE device_id = @deviceId AND pairing_code = @code;";
        update.Parameters.Add("@code", MySqlDbType.VarChar).Value = pairingCode;
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        update.CommandText = """
            DELETE s FROM ac_ir_state s INNER JOIN biometric_devices d ON d.id = s.device_id WHERE d.device_id = @deviceId;
            """;
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await FindBiometricDeviceByIdAsync(deviceId, cancellationToken);
    }

    public async Task<OperationResult> CreateBiometricDeviceAsync(CreateBiometricDeviceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ClassroomId is not null && request.TargetTemperature is not (>= 16 and <= 30))
        {
            return OperationResult.Failure("Target temperature must be between 16°C and 30°C for an assigned device.");
        }

        if (request.StatusPollingIntervalSeconds is < BiometricDeviceSettingsPolicy.MinStatusPollingIntervalSeconds
            or > BiometricDeviceSettingsPolicy.MaxStatusPollingIntervalSeconds)
        {
            return OperationResult.Failure("Enter a valid status polling interval.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.Add("@classroomId", MySqlDbType.Int64).Value =
                request.ClassroomId is long classroomId ? classroomId : DBNull.Value;
            command.Parameters.Add("@isActive", MySqlDbType.Bool).Value = request.IsActive;
            command.Parameters.Add("@pollInterval", MySqlDbType.UInt16).Value = request.StatusPollingIntervalSeconds;
            command.Parameters.Add("@enrollmentEnabled", MySqlDbType.Bool).Value = request.FingerprintEnrollmentEnabled;
            command.Parameters.Add("@scanningEnabled", MySqlDbType.Bool).Value = request.AttendanceScanEnabled;
            command.CommandText = request.ClassroomId is null
                ? @"
                    INSERT INTO biometric_devices (
                        classroom_id, device_id, display_name, is_active,
                        status_poll_interval_seconds, fingerprint_enrollment_enabled,
                        attendance_scan_enabled)
                    VALUES (NULL, UUID(), '', @isActive, @pollInterval, @enrollmentEnabled, @scanningEnabled);"
                : @"
                    INSERT INTO biometric_devices (
                        classroom_id, device_id, display_name, is_active,
                        status_poll_interval_seconds, fingerprint_enrollment_enabled,
                        attendance_scan_enabled)
                    SELECT c.id, UUID(), '', @isActive, @pollInterval, @enrollmentEnabled, @scanningEnabled
                    FROM classrooms c
                    WHERE c.id = @classroomId AND c.is_active = TRUE;";
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("Classroom was not found or is inactive.");
            }

            var deviceId = command.LastInsertedId;
            await using (var updateName = connection.CreateCommand())
            {
                updateName.Transaction = transaction;
                updateName.CommandText = @"
                    UPDATE biometric_devices
                    SET device_id = CAST(1000000000 + id AS CHAR),
                        display_name = CONCAT('Device ', id)
                    WHERE id = @deviceId;";
                updateName.Parameters.Add("@deviceId", MySqlDbType.Int64).Value = deviceId;
                await updateName.ExecuteNonQueryAsync(cancellationToken);
            }

            await AssignNewPairingCodeAsync(connection, transaction, deviceId, cancellationToken);

            if (request.ClassroomId is long assignedClassroomId && request.TargetTemperature is int targetTemperature)
            {
                await using var updateClassroom = connection.CreateCommand();
                updateClassroom.Transaction = transaction;
                updateClassroom.CommandText = @"
                    UPDATE classrooms
                    SET target_temperature = @targetTemperature
                    WHERE id = @classroomId;";
                updateClassroom.Parameters.Add("@targetTemperature", MySqlDbType.Int32).Value = targetTemperature;
                updateClassroom.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = assignedClassroomId;
                await updateClassroom.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("That device code or classroom already has a scanner assigned.");
        }
    }

    public async Task<OperationResult> UpdateBiometricDeviceAsync(
        UpdateBiometricDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ClassroomId is not null && request.TargetTemperature is not (>= 16 and <= 30))
        {
            return OperationResult.Failure("Target temperature must be between 16°C and 30°C for an assigned device.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long? currentClassroomId;
            await using (var findDevice = connection.CreateCommand())
            {
                findDevice.Transaction = transaction;
                findDevice.CommandText = @"
                    SELECT classroom_id
                    FROM biometric_devices
                    WHERE id = @deviceId
                    FOR UPDATE;";
                findDevice.Parameters.Add("@deviceId", MySqlDbType.Int64).Value = request.Id;
                var result = await findDevice.ExecuteScalarAsync(cancellationToken);
                if (result is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The selected device no longer exists.");
                }

                currentClassroomId = result is DBNull ? null : Convert.ToInt64(result);
            }

            if (request.ClassroomId is long requestedClassroomId && requestedClassroomId != currentClassroomId)
            {
                await using var findClassroom = connection.CreateCommand();
                findClassroom.Transaction = transaction;
                findClassroom.CommandText = @"
                    SELECT id
                    FROM classrooms
                    WHERE id = @classroomId AND is_active = TRUE
                    FOR UPDATE;";
                findClassroom.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = requestedClassroomId;
                if (await findClassroom.ExecuteScalarAsync(cancellationToken) is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("Select an active classroom for this device.");
                }
            }

            var assignmentChanged = currentClassroomId != request.ClassroomId;
            if (assignmentChanged || !request.IsActive)
            {
                if (await HasInFlightIrAsync(connection, transaction, request.Id, cancellationToken))
                    return OperationResult.Failure("This scanner has an unresolved IR command. Stop and reconcile it before changing its assignment or status.");
                await InvalidateIrAsync(connection, transaction, request.Id, cancellationToken);
            }
            if ((assignmentChanged || !request.IsActive || !request.FingerprintEnrollmentEnabled) &&
                await HasFingerprintWriteForAsync(connection, transaction, "p.device_id", request.Id, cancellationToken))
                return OperationResult.Failure("This scanner has an unresolved fingerprint write.");
            if (assignmentChanged || !request.IsActive || !request.AttendanceScanEnabled)
            {
                await using var activeSession = connection.CreateCommand();
                activeSession.Transaction = transaction;
                activeSession.CommandText = @"
                    SELECT id
                    FROM attendance_logs
                    WHERE ((@currentClassroomId IS NOT NULL AND classroom_id = @currentClassroomId)
                       OR (@targetClassroomId IS NOT NULL AND classroom_id = @targetClassroomId))
                      AND time_out_utc IS NULL
                      AND status = 'Active'
                    LIMIT 1
                    FOR UPDATE;";
                activeSession.Parameters.Add("@currentClassroomId", MySqlDbType.Int64).Value = currentClassroomId is long oldClassroomId ? oldClassroomId : DBNull.Value;
                activeSession.Parameters.Add("@targetClassroomId", MySqlDbType.Int64).Value = request.ClassroomId is long newClassroomId ? newClassroomId : DBNull.Value;
                if (await activeSession.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("End active class sessions in the affected classroom(s) before changing this device assignment or scan setting.");
                }
            }

            await using (var updateDevice = connection.CreateCommand())
            {
                updateDevice.Transaction = transaction;
                updateDevice.CommandText = @"
                    UPDATE biometric_devices
                    SET classroom_id = @classroomId,
                        display_name = CONCAT('Device ', id),
                        is_active = @isActive,
                        status_poll_interval_seconds = @pollInterval,
                        fingerprint_enrollment_enabled = @enrollmentEnabled,
                        attendance_scan_enabled = @scanningEnabled
                    WHERE id = @deviceId;";
                updateDevice.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = request.ClassroomId is long classroomId ? classroomId : DBNull.Value;
                updateDevice.Parameters.Add("@isActive", MySqlDbType.Bool).Value = request.IsActive;
                updateDevice.Parameters.Add("@pollInterval", MySqlDbType.UInt16).Value = request.StatusPollingIntervalSeconds;
                updateDevice.Parameters.Add("@enrollmentEnabled", MySqlDbType.Bool).Value = request.FingerprintEnrollmentEnabled;
                updateDevice.Parameters.Add("@scanningEnabled", MySqlDbType.Bool).Value = request.AttendanceScanEnabled;
                updateDevice.Parameters.Add("@deviceId", MySqlDbType.Int64).Value = request.Id;
                await updateDevice.ExecuteNonQueryAsync(cancellationToken);
            }

            if (request.ClassroomId is long assignedClassroomId && request.TargetTemperature is int targetTemperature)
            {
                await using var updateClassroom = connection.CreateCommand();
                updateClassroom.Transaction = transaction;
                updateClassroom.CommandText = @"
                    UPDATE classrooms
                    SET target_temperature = @targetTemperature
                    WHERE id = @classroomId;";
                updateClassroom.Parameters.Add("@targetTemperature", MySqlDbType.Int32).Value = targetTemperature;
                updateClassroom.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = assignedClassroomId;
                await updateClassroom.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("That classroom already has a scanner assigned.");
        }
    }

    public async Task<OperationResult> DeleteBiometricDeviceAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long? classroomId;
        await using (var device = connection.CreateCommand())
        {
            device.Transaction = transaction;
            device.CommandText = "SELECT classroom_id FROM biometric_devices WHERE id = @id FOR UPDATE;";
            device.Parameters.Add("@id", MySqlDbType.Int64).Value = id;
            var result = await device.ExecuteScalarAsync(cancellationToken);
            if (result is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("The selected device no longer exists.");
            }

            classroomId = result is DBNull ? null : Convert.ToInt64(result);
        }

        if (await HasInFlightIrAsync(connection, transaction, id, cancellationToken))
            return OperationResult.Failure("This scanner has an unresolved IR command. Stop and reconcile it before deleting the device.");

        if (classroomId is long assignedClassroomId)
        {
            await using var activeSession = connection.CreateCommand();
            activeSession.Transaction = transaction;
            activeSession.CommandText = @"
                SELECT id FROM attendance_logs
                WHERE classroom_id = @classroomId AND time_out_utc IS NULL AND status = 'Active'
                LIMIT 1 FOR UPDATE;";
            activeSession.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = assignedClassroomId;
            if (await activeSession.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("End the active classroom session before deleting its device.");
            }

            await using var room = connection.CreateCommand();
            room.Transaction = transaction;
            room.CommandText = "SELECT ac_status FROM classrooms WHERE id = @classroomId FOR UPDATE;";
            room.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = assignedClassroomId;
            var acStatus = await room.ExecuteScalarAsync(cancellationToken) as string;
            if (acStatus is "Cooling" or "Override" or "Remote")
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("Turn off the classroom AC before deleting its device.");
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM biometric_devices WHERE id = @id;";
            delete.Parameters.Add("@id", MySqlDbType.Int64).Value = id;
            try
            {
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.RowIsReferenced2)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("This scanner has an unresolved fingerprint write or other dependent records.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<bool> HasActiveAttendanceAsync(long teacherAccountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT EXISTS(
                SELECT 1 FROM attendance_logs
                WHERE teacher_account_id = @teacherId AND time_out_utc IS NULL
            );";
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0;
    }

    public async Task<long?> GetActiveAttendanceClassroomIdAsync(long teacherAccountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT classroom_id
            FROM attendance_logs
            WHERE teacher_account_id = @teacherId AND time_out_utc IS NULL
            ORDER BY time_in_utc DESC
            LIMIT 1;";
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async Task<long?> FindInWindowScheduleIdAsync(long teacherAccountId, long classroomId, DateOnly schoolDate, TimeOnly schoolTime, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT cs.id
            FROM class_schedules cs
            INNER JOIN semesters s ON s.id = cs.semester_id AND s.is_active = TRUE
            INNER JOIN teacher_semesters ts
                ON ts.semester_id = cs.semester_id AND ts.teacher_account_id = cs.teacher_account_id
            WHERE cs.teacher_account_id = @teacherId
              AND cs.classroom_id = @classroomId
              AND cs.schedule_date = @schoolDate
              AND @schoolTime >= cs.start_time
              AND @schoolTime < cs.end_time
              AND cs.classroom_id IS NOT NULL
              AND @schoolDate BETWEEN s.start_date AND s.end_date
            ORDER BY CASE cs.schedule_kind WHEN 'Makeup' THEN 0 ELSE 1 END, cs.start_time
            LIMIT 1;";
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        command.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = classroomId;
        command.Parameters.Add("@schoolDate", MySqlDbType.Date).Value = schoolDate;
        command.Parameters.Add("@schoolTime", MySqlDbType.Time).Value = schoolTime;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async Task<DeviceRoomStatusResult> GetDeviceRoomStatusAsync(
        string deviceId,
        DateOnly schoolDate,
        TimeOnly schoolTime,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        long? classroomId;
        string? classroomName;
        string acStatus;
        string resolvedDeviceId;
        string deviceDisplayName;
        int? targetTemperature;
        int statusPollingIntervalSeconds;
        bool fingerprintEnrollmentEnabled;
        bool attendanceScanEnabled;
        bool clearFingerprints;
        await using (var device = connection.CreateCommand())
        {
            device.CommandText = @"
                SELECT d.classroom_id, c.name, c.ac_status, d.device_id, d.pending_clear_fingerprints,
                       d.display_name, c.target_temperature, d.status_poll_interval_seconds,
                       d.fingerprint_enrollment_enabled, d.attendance_scan_enabled
                FROM biometric_devices d
                LEFT JOIN classrooms c ON c.id = d.classroom_id
                WHERE d.device_id = @deviceId AND d.is_active = TRUE
                  AND (d.classroom_id IS NULL OR c.is_active = TRUE)
                LIMIT 1;";
            device.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId.Trim().ToUpperInvariant();
            await using var reader = await device.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new DeviceRoomStatusResult(false, "Unknown or inactive biometric device.");
            }

            classroomId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            classroomName = reader.IsDBNull(1) ? null : reader.GetString(1);
            acStatus = reader.IsDBNull(2) ? "Off" : reader.GetString(2);
            resolvedDeviceId = reader.GetString(3);
            clearFingerprints = reader.GetBoolean(4);
            deviceDisplayName = reader.GetString(5);
            targetTemperature = reader.IsDBNull(6) ? null : reader.GetInt32(6);
            statusPollingIntervalSeconds = reader.GetInt32(7);
            fingerprintEnrollmentEnabled = reader.GetBoolean(8);
            attendanceScanEnabled = reader.GetBoolean(9);
        }

        if (classroomId is not long assignedClassroomId)
        {
            return new DeviceRoomStatusResult(
                true,
                DeviceId: resolvedDeviceId,
                ClassroomId: null,
                ClassroomName: null,
                AcStatus: "Off",
                AcOn: false,
                SessionActive: false,
                ClearFingerprints: clearFingerprints,
                DeviceDisplayName: deviceDisplayName,
                TargetTemperature: null,
                StatusPollingIntervalSeconds: statusPollingIntervalSeconds,
                FingerprintEnrollmentEnabled: fingerprintEnrollmentEnabled,
                AttendanceScanEnabled: attendanceScanEnabled);
        }

        var acOn = acStatus is "Cooling" or "Override" or "Remote";
        long? teacherId = null;
        string? teacherName = null;
        long? scheduleId = null;
        var sessionActive = false;

        await using (var active = connection.CreateCommand())
        {
            active.CommandText = @"
                SELECT a.teacher_account_id, teacher.display_name, a.class_schedule_id
                FROM attendance_logs a
                INNER JOIN user_accounts teacher ON teacher.id = a.teacher_account_id
                WHERE a.classroom_id = @classroomId
                  AND a.time_out_utc IS NULL
                  AND a.status = 'Active'
                ORDER BY a.time_in_utc DESC
                LIMIT 1;";
            active.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = assignedClassroomId;
            await using var reader = await active.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                sessionActive = true;
                teacherId = reader.GetInt64(0);
                teacherName = reader.GetString(1);
                scheduleId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }
        }

        if (!sessionActive && acOn)
        {
            // AC-only re-entry: prefer teacher of the in-window schedule that already has attendance.
            await using var inWindow = connection.CreateCommand();
            inWindow.CommandText = @"
                SELECT a.teacher_account_id, teacher.display_name, a.class_schedule_id
                FROM attendance_logs a
                INNER JOIN user_accounts teacher ON teacher.id = a.teacher_account_id
                INNER JOIN class_schedules schedule ON schedule.id = a.class_schedule_id
                WHERE a.classroom_id = @classroomId
                  AND schedule.schedule_date = @schoolDate
                  AND @schoolTime >= schedule.start_time
                  AND @schoolTime < schedule.end_time
                ORDER BY a.time_in_utc DESC
                LIMIT 1;";
            inWindow.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = assignedClassroomId;
            inWindow.Parameters.Add("@schoolDate", MySqlDbType.Date).Value = schoolDate;
            inWindow.Parameters.Add("@schoolTime", MySqlDbType.Time).Value = schoolTime;
            await using var reader = await inWindow.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                teacherId = reader.GetInt64(0);
                teacherName = reader.GetString(1);
                scheduleId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }
        }

        if (acOn && string.IsNullOrWhiteSpace(teacherName))
        {
            // Prefer last teacher actor from temperature logs.
            await using var log = connection.CreateCommand();
            log.CommandText = @"
                SELECT teacher.id, teacher.display_name
                FROM temperature_logs t
                INNER JOIN user_accounts teacher ON teacher.id = t.actor_account_id AND teacher.role = 'Teacher'
                WHERE t.classroom_id = @classroomId
                  AND t.event_type IN ('ClassStarted', 'AcReentryOn', 'TargetSet')
                ORDER BY t.recorded_utc DESC
                LIMIT 1;";
            log.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = assignedClassroomId;
            await using var reader = await log.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                teacherId = reader.GetInt64(0);
                teacherName = reader.GetString(1);
            }
        }

        if (acOn && string.IsNullOrWhiteSpace(teacherName) && acStatus is "Remote" or "Override")
        {
            teacherName = acStatus == "Remote" ? "Remote AC" : "Override";
        }

        return new DeviceRoomStatusResult(
            true,
            null,
            resolvedDeviceId,
            assignedClassroomId,
            classroomName,
            acStatus,
            acOn,
            sessionActive,
            teacherId,
            teacherName,
            scheduleId,
            clearFingerprints,
            deviceDisplayName,
            targetTemperature,
            statusPollingIntervalSeconds,
            fingerprintEnrollmentEnabled,
            attendanceScanEnabled);
    }

    public async Task<OperationResult> ClearRoomSessionByDeviceIdAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return OperationResult.Failure("deviceId is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long? classroomId;
            await using (var device = connection.CreateCommand())
            {
                device.Transaction = transaction;
                device.CommandText = @"
                    SELECT d.classroom_id
                    FROM biometric_devices d
                    LEFT JOIN classrooms c ON c.id = d.classroom_id
                    WHERE d.device_id = @deviceId AND d.is_active = TRUE
                      AND (d.classroom_id IS NULL OR c.is_active = TRUE)
                    LIMIT 1
                    FOR UPDATE;";
                device.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId.Trim().ToUpperInvariant();
                var id = await device.ExecuteScalarAsync(cancellationToken);
                if (id is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("Unknown or inactive biometric device.");
                }

                classroomId = id is DBNull ? null : Convert.ToInt64(id);
            }

            var recordedAt = SchoolNow();
            var sessions = new List<(long AttendanceId, long TeacherId)>();
            if (classroomId is long assignedClassroomId)
            {
                await using (var find = connection.CreateCommand())
                {
                    find.Transaction = transaction;
                    find.CommandText = @"
                        SELECT id, teacher_account_id
                        FROM attendance_logs
                        WHERE classroom_id = @classroomId
                          AND time_out_utc IS NULL
                          AND status = 'Active'
                        FOR UPDATE;";
                    find.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = assignedClassroomId;
                    await using var reader = await find.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        sessions.Add((reader.GetInt64(0), reader.GetInt64(1)));
                    }
                }

                foreach (var session in sessions)
                {
                    await using var close = connection.CreateCommand();
                    close.Transaction = transaction;
                    close.CommandText = @"
                        UPDATE attendance_logs
                        SET time_out_utc = @recordedAt,
                            status = 'Completed',
                            outcome = COALESCE(outcome, 'OnTime')
                        WHERE id = @id AND time_out_utc IS NULL;";
                    close.Parameters.Add("@id", MySqlDbType.Int64).Value = session.AttendanceId;
                    close.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = recordedAt;
                    await close.ExecuteNonQueryAsync(cancellationToken);
                }

                var actorId = sessions.Count > 0 ? sessions[0].TeacherId : (long?)null;
                await SetClassroomStatusAndLogAsync(
                    connection,
                    transaction,
                    assignedClassroomId,
                    actorId,
                    "Off",
                    "ClassEnded",
                    "Device DELETEALL: fingerprints cleared — session and AC reset",
                    recordedAt,
                    cancellationToken);
            }

            await using (var clearPending = connection.CreateCommand())
            {
                clearPending.Transaction = transaction;
                clearPending.CommandText = @"
                    DELETE i FROM fingerprint_sensor_inventory i
                    INNER JOIN biometric_devices d ON d.id = i.device_id WHERE d.device_id = @deviceId;
                    DELETE c FROM fingerprint_enrollment_challenges c
                    INNER JOIN biometric_devices d ON d.id = c.device_id
                    WHERE d.device_id = @deviceId AND c.operation_name = 'inventory';
                    UPDATE biometric_devices
                    SET pending_clear_fingerprints = 0
                    WHERE device_id = @deviceId AND is_active = TRUE;";
                clearPending.Parameters.Add("@deviceId", MySqlDbType.VarChar).Value = deviceId.Trim().ToUpperInvariant();
                await clearPending.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<OperationResult> RequestClearAllFingerprintDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await HasUnresolvedFingerprintWritesAsync(connection, transaction, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("Resolve outstanding fingerprint writes before clearing scanner data.");
            }
            await using (var invalidate = connection.CreateCommand())
            {
                invalidate.Transaction = transaction;
                invalidate.CommandText = @"DELETE FROM fingerprint_sensor_inventory;
                    DELETE FROM fingerprint_enrollment_challenges WHERE operation_name = 'inventory';";
                await invalidate.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var wipe = connection.CreateCommand())
            {
                wipe.Transaction = transaction;
                wipe.CommandText = "DELETE FROM fingerprint_templates;";
                await wipe.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var pending = connection.CreateCommand())
            {
                pending.Transaction = transaction;
                pending.CommandText = @"
                    UPDATE biometric_devices
                    SET pending_clear_fingerprints = 1
                    WHERE is_active = TRUE;";
                await pending.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<bool> HasUnresolvedFingerprintWritesAsync(MySqlConnection connection, MySqlTransaction transaction, CancellationToken cancellationToken)
    {
        await LockFingerprintDevicesAsync(connection, transaction, cancellationToken);
        await using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandText = "SELECT preparation_id FROM fingerprint_enrollment_write_guards LIMIT 1 FOR UPDATE;";
        return await guard.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task LockFingerprintDevicesAsync(MySqlConnection connection, MySqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using (var devices = connection.CreateCommand())
        {
            devices.Transaction = transaction;
            devices.CommandText = "SELECT id FROM biometric_devices ORDER BY id FOR UPDATE;";
            await using var reader = await devices.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) { }
        }
    }

    private static async Task<bool> HasInFlightIrAsync(MySqlConnection connection, MySqlTransaction transaction,
        long deviceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT device_id FROM ac_ir_state WHERE device_id = @id AND state = 'inflight' FOR UPDATE;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasInFlightIrForRoomAsync(MySqlConnection connection, MySqlTransaction transaction,
        long roomId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.device_id FROM ac_ir_state s INNER JOIN biometric_devices d ON d.id = s.device_id
            WHERE d.classroom_id = @room AND s.state = 'inflight' LIMIT 1 FOR UPDATE;
            """;
        command.Parameters.Add("@room", MySqlDbType.Int64).Value = roomId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InvalidateIrAsync(MySqlConnection connection, MySqlTransaction transaction,
        long deviceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ac_ir_state WHERE device_id = @id;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = deviceId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InvalidateIrForRoomAsync(MySqlConnection connection, MySqlTransaction transaction,
        long roomId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE s FROM ac_ir_state s INNER JOIN biometric_devices d ON d.id = s.device_id WHERE d.classroom_id = @room;
            """;
        command.Parameters.Add("@room", MySqlDbType.Int64).Value = roomId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasFingerprintWriteForAsync(
        MySqlConnection connection, MySqlTransaction transaction, string field, long id, CancellationToken cancellationToken)
    {
        if (field is not ("p.teacher_account_id" or "d.classroom_id" or "p.device_id"))
            throw new ArgumentOutOfRangeException(nameof(field));
        await using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandText = $@"SELECT g.preparation_id
            FROM fingerprint_enrollment_write_guards g
            INNER JOIN fingerprint_enrollment_preparations p ON p.id = g.preparation_id
            INNER JOIN biometric_devices d ON d.id = p.device_id
            WHERE {field} = @id LIMIT 1 FOR UPDATE;";
        guard.Parameters.Add("@id", MySqlDbType.Int64).Value = id;
        return await guard.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<int> CountFingerprintTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fingerprint_templates;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    public async Task<AcReentryResult?> TryInWindowAcReentryAsync(
        long teacherAccountId,
        long scheduleId,
        long classroomId,
        DateOnly schoolDate,
        TimeOnly schoolTime,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Still inside this teacher's schedule window; a real (non-Absent) punch already exists.
            // Caller only reaches here when the teacher has no open session (logout already recorded).
            await using var eligible = connection.CreateCommand();
            eligible.Transaction = transaction;
            eligible.CommandText = @"
                SELECT c.ac_status
                FROM class_schedules schedule
                INNER JOIN semesters semester ON semester.id = schedule.semester_id AND semester.is_active = TRUE
                INNER JOIN teacher_semesters enrollment
                    ON enrollment.semester_id = schedule.semester_id
                   AND enrollment.teacher_account_id = schedule.teacher_account_id
                INNER JOIN classrooms c ON c.id = schedule.classroom_id AND c.is_active = TRUE
                WHERE schedule.id = @scheduleId
                  AND schedule.teacher_account_id = @teacherId
                  AND schedule.classroom_id = @classroomId
                  AND schedule.schedule_date = @schoolDate
                  AND @schoolTime >= schedule.start_time
                  AND @schoolTime < schedule.end_time
                  AND @schoolDate BETWEEN semester.start_date AND semester.end_date
                  AND EXISTS (
                        SELECT 1
                        FROM attendance_logs prior
                        WHERE prior.class_schedule_id = schedule.id
                          AND prior.teacher_account_id = @teacherId
                          AND prior.verification_method <> 'System'
                          AND COALESCE(prior.outcome, '') <> 'Absent'
                  )
                  AND NOT EXISTS (
                        SELECT 1
                        FROM attendance_logs active
                        WHERE active.teacher_account_id = @teacherId
                          AND active.time_out_utc IS NULL
                  )
                FOR UPDATE;";
            eligible.Parameters.Add("@scheduleId", MySqlDbType.Int64).Value = scheduleId;
            eligible.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
            eligible.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = classroomId;
            eligible.Parameters.Add("@schoolDate", MySqlDbType.Date).Value = schoolDate;
            eligible.Parameters.Add("@schoolTime", MySqlDbType.Time).Value = schoolTime;

            var currentStatus = await eligible.ExecuteScalarAsync(cancellationToken) as string;
            if (string.IsNullOrWhiteSpace(currentStatus))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var turnOn = currentStatus is not ("Cooling" or "Override" or "Remote");
            var nextStatus = turnOn ? "Cooling" : "Off";
            var action = turnOn ? "ac_on" : "ac_off";
            var eventType = turnOn ? "AcReentryOn" : "AcReentryOff";
            var recordedAt = SchoolNow();
            var notes = turnOn
                ? "In-window re-login: reopened attendance (cleared time-out)"
                : "In-window logout: updated attendance time-out";

            if (turnOn)
            {
                // Relogin: reopen the schedule's attendance row so the teacher is Active again.
                await using var reopen = connection.CreateCommand();
                reopen.Transaction = transaction;
                reopen.CommandText = @"
                    UPDATE attendance_logs
                    SET time_out_utc = NULL,
                        status = 'Active'
                    WHERE class_schedule_id = @scheduleId
                      AND teacher_account_id = @teacherId
                      AND verification_method <> 'System'
                      AND COALESCE(outcome, '') <> 'Absent'
                    ORDER BY time_in_utc DESC
                    LIMIT 1;";
                reopen.Parameters.Add("@scheduleId", MySqlDbType.Int64).Value = scheduleId;
                reopen.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
                if (await reopen.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
            }
            else
            {
                // Logout while AC still Cooling but session already closed: refresh time-out to now.
                await using var touchOut = connection.CreateCommand();
                touchOut.Transaction = transaction;
                touchOut.CommandText = @"
                    UPDATE attendance_logs
                    SET time_out_utc = @recordedAt,
                        status = 'Completed',
                        outcome = COALESCE(outcome, 'OnTime')
                    WHERE class_schedule_id = @scheduleId
                      AND teacher_account_id = @teacherId
                      AND verification_method <> 'System'
                      AND COALESCE(outcome, '') <> 'Absent'
                    ORDER BY time_in_utc DESC
                    LIMIT 1;";
                touchOut.Parameters.Add("@scheduleId", MySqlDbType.Int64).Value = scheduleId;
                touchOut.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
                touchOut.Parameters.Add("@recordedAt", MySqlDbType.DateTime).Value = recordedAt;
                await touchOut.ExecuteNonQueryAsync(cancellationToken);
            }

            await SetClassroomStatusAndLogAsync(
                connection,
                transaction,
                classroomId,
                teacherAccountId,
                nextStatus,
                eventType,
                notes,
                recordedAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AcReentryResult(action, nextStatus);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
