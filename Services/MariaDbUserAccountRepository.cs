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
                INSERT INTO user_accounts (username, display_name, employee_number, device_pin, email, password_hash, role)
                VALUES (@username, @displayName, @employeeNumber, @devicePin, @email, @passwordHash, 'Teacher');";
            account.Parameters.Add("@username", MySqlDbType.VarChar).Value = request.Username;
            account.Parameters.Add("@displayName", MySqlDbType.VarChar).Value = request.DisplayName;
            account.Parameters.Add("@employeeNumber", MySqlDbType.VarChar).Value = request.EmployeeNumber;
            account.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = await ResolveNewTeacherPinAsync(connection, transaction, request.DevicePin, cancellationToken);
            account.Parameters.Add("@email", MySqlDbType.VarChar).Value = request.Email;
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
            return OperationResult.Failure("That username, employee number, PIN, or email is already registered.");
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
                    SET display_name = @displayName, username = @username, employee_number = @employeeNumber, email = @email
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
            }
            else if (passwordHash is not null && devicePin is null)
            {
                command.CommandText = @"
                    UPDATE user_accounts
                    SET display_name = @displayName, username = @username, employee_number = @employeeNumber, email = @email, password_hash = @passwordHash
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
                command.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash;
            }
            else if (passwordHash is null && devicePin is not null)
            {
                command.CommandText = @"
                    UPDATE user_accounts
                    SET display_name = @displayName, username = @username, employee_number = @employeeNumber, email = @email, device_pin = @devicePin
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
                command.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = devicePin;
            }
            else
            {
                command.CommandText = @"
                    UPDATE user_accounts
                    SET display_name = @displayName, username = @username, employee_number = @employeeNumber, email = @email, device_pin = @devicePin, password_hash = @passwordHash
                    WHERE id = @id AND role = 'Teacher' AND is_active = TRUE;";
                command.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = devicePin!;
                command.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash!;
            }

            command.Parameters.Add("@id", MySqlDbType.Int64).Value = request.AccountId;
            command.Parameters.Add("@displayName", MySqlDbType.VarChar).Value = request.DisplayName;
            command.Parameters.Add("@username", MySqlDbType.VarChar).Value = request.Username;
            command.Parameters.Add("@employeeNumber", MySqlDbType.VarChar).Value = request.EmployeeNumber;
            command.Parameters.Add("@email", MySqlDbType.VarChar).Value = request.Email;

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
            return OperationResult.Failure("That username, employee number, PIN, or email is already registered.");
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
            SELECT u.id, u.display_name, u.username, u.employee_number, u.email, COUNT(f.id)
            FROM user_accounts u LEFT JOIN fingerprint_templates f ON f.user_account_id = u.id
            WHERE u.role = 'Teacher' AND u.is_active = TRUE
            GROUP BY u.id, u.display_name, u.username, u.employee_number, u.email ORDER BY u.display_name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new TeacherDirectoryEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                Convert.ToInt32(reader.GetValue(5))));
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
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? OperationResult.Success()
            : OperationResult.Failure("The teacher account was not found.");
    }

    public async Task<IReadOnlyList<ClassroomEntry>> GetClassroomsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<ClassroomEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, name, capacity, target_temperature, current_temperature, ac_status, is_active
            FROM classrooms WHERE is_active = TRUE ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ClassroomEntry(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.GetString(5), reader.GetBoolean(6)));
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
                INSERT INTO classrooms (name, capacity, target_temperature, current_temperature, ac_status)
                VALUES (@name, @capacity, @targetTemperature, @targetTemperature, 'Idle');";
            AddClassroomParameters(command, request.Name, request.Capacity, request.TargetTemperature);
            await command.ExecuteNonQueryAsync(cancellationToken);
            var classroomId = command.LastInsertedId;

            var deviceCode = request.Name.Trim().ToUpperInvariant().Replace(' ', '-');
            await using var device = connection.CreateCommand();
            device.Transaction = transaction;
            device.CommandText = @"
                INSERT INTO biometric_devices (classroom_id, device_code, display_name, is_active)
                VALUES (@classroomId, @deviceCode, @displayName, TRUE);";
            device.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = classroomId;
            device.Parameters.Add("@deviceCode", MySqlDbType.VarChar).Value = deviceCode;
            device.Parameters.Add("@displayName", MySqlDbType.VarChar).Value = $"{request.Name.Trim()} scanner";
            await device.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Failure("A classroom or scanner with that name/code already exists.");
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE classrooms
            SET name = @name, capacity = @capacity, target_temperature = @targetTemperature, is_active = @isActive
            WHERE id = @id;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = request.Id;
        AddClassroomParameters(command, request.Name, request.Capacity, request.TargetTemperature);
        command.Parameters.Add("@isActive", MySqlDbType.Bool).Value = request.IsActive;
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OperationResult.Success()
                : OperationResult.Failure("The classroom was not found.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("A classroom with that name already exists.");
        }
    }

    public async Task<OperationResult> DeleteClassroomAsync(long classroomId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using (var scheduled = connection.CreateCommand())
        {
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
        command.CommandText = @"
            UPDATE classrooms
            SET is_active = FALSE, ac_status = 'Retired'
            WHERE id = @id AND is_active = TRUE;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = classroomId;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? OperationResult.Success()
            : OperationResult.Failure("The active classroom was not found.");
    }

    public async Task<OperationResult> CreateScheduleAsync(CreateScheduleRequest request, CancellationToken cancellationToken = default)
    {
        if (!ScheduleKinds.IsValid(request.ScheduleKind))
        {
            return OperationResult.Failure("Schedule kind must be Regular or Makeup.");
        }

        if (request.SemesterId <= 0)
        {
            return OperationResult.Failure("Select a semester for this schedule.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
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
                    return OperationResult.Failure("The selected semester was not found.");
                }

                var startDate = reader.GetFieldValue<DateOnly>(0);
                var endDate = reader.GetFieldValue<DateOnly>(1);
                var isActive = reader.GetBoolean(2);
                await reader.DisposeAsync();

                if (!isActive)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The selected semester is inactive.");
                }

                if (request.ScheduleDate < startDate || request.ScheduleDate > endDate)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return OperationResult.Failure("The schedule date must fall within the semester date range.");
                }
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
                    return OperationResult.Failure("Enroll the teacher in this semester before creating a schedule.");
                }
            }

            await using var conflict = connection.CreateCommand();
            conflict.Transaction = transaction;
            conflict.CommandText = @"
                SELECT EXISTS(
                    SELECT 1 FROM class_schedules
                    WHERE schedule_date = @date
                      AND (teacher_account_id = @teacherId OR classroom_id = @classroomId)
                      AND start_time < @endTime AND end_time > @startTime
                );";
            AddScheduleParameters(conflict, request);
            if (Convert.ToInt64(await conflict.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("The selected teacher or classroom already has an overlapping schedule.");
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
            AddScheduleParameters(create, request);
            if (await create.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OperationResult.Failure("The selected teacher or classroom is no longer available.");
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
                : OperationResult.Failure("Teacher or semester was not found.");
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
            SELECT u.id, u.display_name, u.username, u.employee_number, u.email, u.phone, u.device_pin, COUNT(f.id)
            FROM user_accounts u LEFT JOIN fingerprint_templates f ON f.user_account_id = u.id
            WHERE u.id = @id AND u.role = 'Teacher' AND u.is_active = TRUE
            GROUP BY u.id, u.display_name, u.username, u.employee_number, u.email, u.phone, u.device_pin;";
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = accountId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TeacherProfileEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Convert.ToInt32(reader.GetValue(7)))
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
        command.Parameters.Add("@email", MySqlDbType.VarChar).Value = request.Email;
        command.Parameters.AddWithValue("@phone", (object?)request.Phone ?? DBNull.Value);
        command.Parameters.Add("@devicePin", MySqlDbType.VarChar).Value = request.DevicePin!;
        if (passwordHash is not null) command.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = passwordHash;
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? OperationResult.Success() : OperationResult.Failure("Your teacher account was not found.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("That email or device PIN is already registered to another account.");
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

            // AC-only re-entry can leave Cooling after attendance closed — turn Off once no schedule is in-window for that room.
            await using var clearAc = connection.CreateCommand();
            clearAc.Transaction = transaction;
            clearAc.CommandText = @"
                SELECT c.id
                FROM classrooms c
                WHERE c.is_active = TRUE
                  AND c.ac_status IN ('Cooling', 'Override')
                  AND NOT EXISTS (
                        SELECT 1
                        FROM attendance_logs a
                        WHERE a.classroom_id = c.id
                          AND a.time_out_utc IS NULL
                          AND a.status = 'Active'
                  )
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
            var status = request.EventType == "HardShutdown" ? "Off" : request.EventType == "EmergencyOverride" ? "Override" : "Cooling";
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
                UPDATE classrooms
                SET target_temperature = @targetTemperature, current_temperature = @targetTemperature, ac_status = @status
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

    public async Task ResetSystemToFirstAccessAsync(DefaultAdminOptions defaultAdmin, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
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

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO fingerprint_templates (user_account_id, finger_position, template_identifier)
            VALUES (@teacherId, @fingerPosition, @templateId)
            ON DUPLICATE KEY UPDATE template_identifier = VALUES(template_identifier);";
        command.Parameters.Add("@teacherId", MySqlDbType.Int64).Value = teacherAccountId;
        command.Parameters.Add("@fingerPosition", MySqlDbType.VarChar).Value = fingerPosition;
        command.Parameters.Add("@templateId", MySqlDbType.Int64).Value = templateId;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
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

        if (profile.FingerprintTemplateCount >= FingerprintSlots.All.Length)
        {
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
                FingerprintTemplateCount: profile.FingerprintTemplateCount);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
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
                    FingerprintTemplateCount: profile.FingerprintTemplateCount);
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
                FingerprintTemplateCount: profile.FingerprintTemplateCount + 1,
                TemplateId: templateId,
                FingerPosition: fingerPosition);
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeviceFingerprintEnrollResult(false, "That fingerprint template ID is already enrolled to another teacher or finger.");
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

    public async Task<FingerprintTeacherMatch?> FindTeacherByFingerprintTemplateIdAsync(long templateId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT u.id, u.display_name, u.username
            FROM fingerprint_templates f
            INNER JOIN user_accounts u ON u.id = f.user_account_id
            WHERE f.template_identifier = @templateId
              AND u.role = 'Teacher'
              AND u.is_active = TRUE
            LIMIT 1;";
        command.Parameters.Add("@templateId", MySqlDbType.Int64).Value = templateId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FingerprintTeacherMatch(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<BiometricDeviceEntry?> FindBiometricDeviceByCodeAsync(string deviceCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT d.id, d.classroom_id, c.name, d.device_code, d.display_name, d.is_active
            FROM biometric_devices d
            INNER JOIN classrooms c ON c.id = d.classroom_id
            WHERE UPPER(d.device_code) = @deviceCode
              AND d.is_active = TRUE
              AND c.is_active = TRUE
            LIMIT 1;";
        command.Parameters.Add("@deviceCode", MySqlDbType.VarChar).Value = deviceCode.Trim().ToUpperInvariant();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BiometricDeviceEntry(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5));
    }

    public async Task<IReadOnlyList<BiometricDeviceEntry>> GetBiometricDevicesAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<BiometricDeviceEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT d.id, d.classroom_id, c.name, d.device_code, d.display_name, d.is_active
            FROM biometric_devices d
            INNER JOIN classrooms c ON c.id = d.classroom_id
            ORDER BY c.name, d.device_code;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BiometricDeviceEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5)));
        }
        return items;
    }

    public async Task<OperationResult> CreateBiometricDeviceAsync(CreateBiometricDeviceRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceCode) || request.DeviceCode.Trim().Length > 64)
        {
            return OperationResult.Failure("Enter a device code of 64 characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 120)
        {
            return OperationResult.Failure("Enter a device name of 120 characters or fewer.");
        }

        if (request.ClassroomId <= 0)
        {
            return OperationResult.Failure("Select a classroom for this scanner.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO biometric_devices (classroom_id, device_code, display_name, is_active)
            SELECT c.id, @deviceCode, @displayName, TRUE
            FROM classrooms c
            WHERE c.id = @classroomId AND c.is_active = TRUE;";
        command.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = request.ClassroomId;
        command.Parameters.Add("@deviceCode", MySqlDbType.VarChar).Value = request.DeviceCode.Trim().ToUpperInvariant();
        command.Parameters.Add("@displayName", MySqlDbType.VarChar).Value = request.DisplayName.Trim();
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OperationResult.Success()
                : OperationResult.Failure("Classroom was not found or is inactive.");
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return OperationResult.Failure("That device code or classroom already has a scanner assigned.");
        }
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
        string deviceCode,
        DateOnly schoolDate,
        TimeOnly schoolTime,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        long classroomId;
        string classroomName;
        string acStatus;
        string resolvedDeviceCode;
        await using (var device = connection.CreateCommand())
        {
            device.CommandText = @"
                SELECT d.classroom_id, c.name, c.ac_status, d.device_code
                FROM biometric_devices d
                INNER JOIN classrooms c ON c.id = d.classroom_id AND c.is_active = TRUE
                WHERE d.device_code = @deviceCode AND d.is_active = TRUE
                LIMIT 1;";
            device.Parameters.Add("@deviceCode", MySqlDbType.VarChar).Value = deviceCode.Trim();
            await using var reader = await device.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new DeviceRoomStatusResult(false, "Unknown or inactive biometric device.");
            }

            classroomId = reader.GetInt64(0);
            classroomName = reader.GetString(1);
            acStatus = reader.GetString(2);
            resolvedDeviceCode = reader.GetString(3);
        }

        var acOn = acStatus is "Cooling" or "Override";
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
            active.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = classroomId;
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
            inWindow.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = classroomId;
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
            // Last AC-on actor from temperature logs (ClassStarted / AcReentryOn).
            await using var log = connection.CreateCommand();
            log.CommandText = @"
                SELECT teacher.id, teacher.display_name
                FROM temperature_logs t
                INNER JOIN user_accounts teacher ON teacher.id = t.actor_account_id
                WHERE t.classroom_id = @classroomId
                  AND t.event_type IN ('ClassStarted', 'AcReentryOn')
                ORDER BY t.recorded_utc DESC
                LIMIT 1;";
            log.Parameters.Add("@classroomId", MySqlDbType.Int64).Value = classroomId;
            await using var reader = await log.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                teacherId = reader.GetInt64(0);
                teacherName = reader.GetString(1);
            }
        }

        return new DeviceRoomStatusResult(
            true,
            null,
            resolvedDeviceCode,
            classroomId,
            classroomName,
            acStatus,
            acOn,
            sessionActive,
            teacherId,
            teacherName,
            scheduleId);
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
            // Still inside this teacher's schedule window for this classroom, and attendance already completed.
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
                          AND prior.time_out_utc IS NOT NULL
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

            var turnOn = !string.Equals(currentStatus, "Cooling", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentStatus, "Override", StringComparison.OrdinalIgnoreCase);
            var nextStatus = turnOn ? "Cooling" : "Off";
            var action = turnOn ? "ac_on" : "ac_off";
            var eventType = turnOn ? "AcReentryOn" : "AcReentryOff";
            var notes = turnOn
                ? "In-window AC re-entry (no new attendance)"
                : "In-window AC off after re-entry";

            await SetClassroomStatusAndLogAsync(
                connection,
                transaction,
                classroomId,
                teacherAccountId,
                nextStatus,
                eventType,
                notes,
                SchoolNow(),
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
