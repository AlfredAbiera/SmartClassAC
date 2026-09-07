# Supabase setup

SmartClass AC stores login accounts, the default administrator, teacher accounts, classroom records, schedules, attendance, leave/overtime/early-out requests, support tickets, and temperature logs in Supabase PostgreSQL. The server-side application uses the Npgsql PostgreSQL driver and creates its required schema on the first successful start.

## Current local setup

The setup helper currently defaults to administrator username `admin`, administrator password `R3m0vabl3!2028`, PostgreSQL port `5432`, and database user `postgres`. Its default Supabase value is a URL; Npgsql requires the hostname only, so use `db.<project-ref>.supabase.co` or the host shown in Supabase **Connect**. The helper supports pooler configuration with `-UseSessionPooler`.

1. Create or open a Supabase project. In **Connect**, copy either the direct connection details (best for a persistent backend on IPv6) or the session-pooler details (for IPv4-only networks). Supabase documents these choices in its [database connection guide](https://supabase.com/docs/guides/database/connecting-to-postgres).
2. Keep the database password and default-administrator password out of `appsettings.json`. For local development, store the Npgsql-formatted connection string and administrator credentials in User Secrets. `SSL Mode=Require` is required; use `SSL Mode=VerifyFull` instead when you have also configured the Supabase root certificate.

   **Quick setup:** run `.\scripts\setup-user-secrets.ps1` from the repo root and follow the prompts.

```powershell
$project = ".\SmartClassAC.csproj"

# Or run the interactive helper:
# .\scripts\setup-user-secrets.ps1

dotnet user-secrets init --project $project
dotnet user-secrets set "ConnectionStrings:SmartClassSupabase" 'Host=db.YOUR_PROJECT_REF.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=YOUR_DATABASE_PASSWORD;SSL Mode=Require;' --project $project
dotnet user-secrets set "DefaultAdmin:Username" "admin" --project $project
dotnet user-secrets set "DefaultAdmin:Password" "R3m0vabl3!2028" --project $project
```

If your environment cannot reach the direct `db.<project-ref>.supabase.co:5432` endpoint, use the host, port, and username from Supabase's **Session pooler** connection with `-UseSessionPooler`. The script defaults to port `5432`; use the exact values shown by Supabase. Do not use the transaction pooler on port `6543` for this persistent Blazor Server application.

3. Configure an SMTP sender for the Login → **Forgot password?** flow. The public base URL must be the exact HTTPS URL where people open this app; it is used in the link sent to the account's registered email. Use the credentials supplied by your school's email provider or a transactional email service—do not place them in `appsettings.json`.

```powershell
dotnet user-secrets set "PasswordReset:PublicBaseUrl" "https://smartclass.example.edu" --project $project
dotnet user-secrets set "PasswordReset:FromAddress" "no-reply@smartclass.example.edu" --project $project
dotnet user-secrets set "PasswordReset:FromName" "SmartClass AC" --project $project
dotnet user-secrets set "PasswordReset:SmtpHost" "smtp.example.edu" --project $project
dotnet user-secrets set "PasswordReset:SmtpPort" "587" --project $project
dotnet user-secrets set "PasswordReset:SmtpUsername" "no-reply@smartclass.example.edu" --project $project
dotnet user-secrets set "PasswordReset:SmtpPassword" "YOUR_SMTP_APP_PASSWORD" --project $project
dotnet user-secrets set "PasswordReset:EnableSsl" "true" --project $project
```

For a deployed app, set the equivalent environment variables: `ConnectionStrings__SmartClassSupabase`, `DefaultAdmin__Username`, `DefaultAdmin__Password`, and `PasswordReset__PublicBaseUrl`, `PasswordReset__FromAddress`, `PasswordReset__FromName`, `PasswordReset__SmtpHost`, `PasswordReset__SmtpPort`, `PasswordReset__SmtpUsername`, `PasswordReset__SmtpPassword`, `PasswordReset__EnableSsl`.

Configure the school's local time zone for schedule and attendance rules. Set `SchoolTime:TimeZoneId` in `appsettings.json` or use the environment variable `SchoolTime__TimeZoneId`. Windows examples are `Singapore Standard Time` and `Tokyo Standard Time`; Linux examples are `Asia/Singapore` and `Asia/Tokyo`. The application falls back to the server's local time zone when this setting is empty or invalid.

4. In **Supabase Dashboard → SQL Editor**, create a new query, paste the complete contents of [SUPABASE-SQL-SETUP.sql](SUPABASE-SQL-SETUP.sql), and run it once. It creates the schema and the database-level schedule-conflict rules. This step is repeat-safe, and the application will also check that the schema exists when it starts.

5. Start the application. It creates the PBKDF2-protected default administrator, if it does not exist. The direct server connection is the only intended database client, so do not put its connection string in browser code.

```powershell
dotnet run --project $project
```

The application retries transient database initialization failures with bounded backoff. After startup, check `/health` on the application URL to verify that Supabase PostgreSQL is reachable.

6. Open `/first-access` once to save the default administrator's recovery email. Then sign in through `/login` using the configured administrator username and password.
7. In the **Admin dashboard**, follow the same order as the flowchart:

   1. **Teachers** — create the teacher account; this writes the login, `Teacher` role, and ten fingerprint template IDs.
   2. **Classrooms** — add each room with capacity and target temperature. This creates the distinct classroom record that was missing before.
   3. **Schedules** — select saved teacher and classroom records, then enter subject/date/times. The system rejects overlapping teacher or classroom schedules before writing, and PostgreSQL enforces the same rule during concurrent saves.
   4. Teachers sign in to their own dashboard. Their name, schedule, profile, attendance, requests, tickets, and assigned classrooms are loaded from Supabase—not from a sample “Maria Santos” record.
   5. **System settings → Reset all system data** — type `RESET` to remove all operational records and restore the configured default administrator with no email. You are redirected to First Access to set it up again.
   6. **Remove records safely** — type `DELETE` in the confirmation dialog. A teacher is permanently deleted from Supabase, including related records; delete or move its current/future schedules first. A classroom is retired so its audit history remains. A deleted schedule is removed while any existing attendance entry remains stored.

8. Test the recovery flow after SMTP is configured: select **Forgot password?** on Login, enter the registered email, open the received link within 15 minutes, then enter and confirm a password with at least 10 characters, upper/lowercase letters, and a number. The reset token is stored only as a SHA-256 hash in Supabase and becomes unusable after it is used.

`SUPABASE-SQL-OPERATIONS.sql` contains optional, commented SQL Editor examples for database administrators who need to validate a classroom/schedule or perform an emergency reset outside the dashboard.

The default administrator password is read only during initial database seeding. Change it from the administrator settings flow after the account exists; changing the environment value later does not overwrite an existing password.

After moving environments, you can remove the unused TiDB secret with:

```powershell
dotnet user-secrets remove "ConnectionStrings:SmartClassTiDbCloud" --project $project
```
