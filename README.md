# SmartClass AC

SmartClass AC is a **Blazor Server** web application for managing smart classroom air-conditioning. It provides administrator and teacher dashboards for classroom records, schedules, attendance, leave/overtime requests, support tickets, and temperature monitoring. All persistent data is stored in **Supabase PostgreSQL**.

## Current local setup

The setup helper currently defaults to administrator username `admin`, administrator password `R3m0vabl3!2028`, PostgreSQL port `5432`, and database user `postgres`. Its default Supabase value is a URL; Npgsql requires the hostname only, so use `db.<project-ref>.supabase.co` or the host shown in Supabase **Connect**. The helper can also be run with `-UseSessionPooler` for a pooler connection.

## Tech stack

| Layer | Technology |
|-------|------------|
| Runtime | .NET 8 (ASP.NET Core) |
| UI | Blazor Server, Razor Pages, Bootstrap 5 |
| Database | Supabase PostgreSQL via Npgsql + EF Core |
| Auth | Custom authentication with PBKDF2 password hashing |
| Email | MailKit (SMTP) for password-reset links |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer
- A [Supabase](https://supabase.com) project with PostgreSQL access
- (Optional) SMTP credentials for the forgot-password flow
- [Cursor](https://cursor.com) or any editor with the **C# Dev Kit** / **C#** extension — Visual Studio is **not** required

## Quick start

### 1. Clone and restore

```powershell
git clone <repository-url>
cd SmartClassAC
dotnet restore
```

### 2. Configure Supabase and secrets

Follow the full guide in [SUPABASE-SETUP.md](SUPABASE-SETUP.md). At minimum:

1. Run [SUPABASE-SQL-SETUP.sql](SUPABASE-SQL-SETUP.sql) once in the Supabase SQL Editor.
2. Store credentials in .NET User Secrets (never commit passwords to `appsettings.json`):

```powershell
.\scripts\setup-user-secrets.ps1
```

Or set them manually:

```powershell
$project = ".\SmartClassAC.csproj"

dotnet user-secrets init --project $project
dotnet user-secrets set "ConnectionStrings:SmartClassSupabase" 'Host=db.YOUR_PROJECT_REF.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=YOUR_DATABASE_PASSWORD;SSL Mode=Require;' --project $project
dotnet user-secrets set "DefaultAdmin:Username" "admin" --project $project
dotnet user-secrets set "DefaultAdmin:Password" "R3m0vabl3!2028" --project $project
```

If your network cannot reach the direct `db.<project-ref>.supabase.co:5432` endpoint, use the **Session pooler** connection details from the Supabase dashboard instead with `-UseSessionPooler`. The script defaults to port `5432`; use the exact host, port, and username shown by Supabase. Do not use the transaction pooler on port `6543` for this persistent Blazor Server application.

### 3. Run the application

```powershell
dotnet run
```

With hot reload during development:

```powershell
dotnet watch run
```

Open **http://localhost:5062** (see `Properties/launchSettings.json` for other profiles).

Check database availability at **http://localhost:5062/health**. A healthy response means Supabase PostgreSQL is reachable; an unavailable response means the database health check failed. Startup retries transient database failures with bounded exponential backoff.

### 4. First-time setup

1. On first start, the app seeds the default administrator account.
2. Visit `/first-access` to save the administrator recovery email.
3. Sign in at `/login` with the configured admin credentials.
4. Use the **Admin dashboard** to create teachers, classrooms, and schedules.

## Running in Cursor IDE

This project is CLI-driven and does not depend on Visual Studio.

1. Open the `SmartClassAC` folder in Cursor.
2. Install the recommended extensions when prompted (**C# Dev Kit** and **C#**).
3. Choose one of these run options:

| Method | How |
|--------|-----|
| **F5 debug** | Run and Debug panel → **SmartClassAC (http)** or **SmartClassAC (https)** |
| **Hot reload** | Terminal: `dotnet watch run` |
| **One-shot** | Terminal: `dotnet run` |

4. (Optional) Trust the HTTPS development certificate:

```powershell
dotnet dev-certs https --trust
```

Launch profiles are defined in `.vscode/launch.json` and match `Properties/launchSettings.json` (HTTP on port **5062**, HTTPS on **7044**).

## Project structure

```
SmartClassAC/
├── .vscode/            # Cursor/VS Code launch and build tasks
├── scripts/            # setup-user-secrets.ps1 helper
├── Pages/              # Blazor pages (Login, Admin, Teacher, etc.)
├── Shared/             # Layout and navigation components
├── Services/           # Auth, database, email, and domain logic
├── wwwroot/            # Static assets (CSS, JS, Bootstrap)
├── Properties/         # launchSettings.json
├── Program.cs          # Application entry point
├── appsettings.json    # Non-secret configuration (empty placeholders)
├── SUPABASE-SETUP.md   # Detailed database and secrets guide
├── SUPABASE-SQL-SETUP.sql
└── SUPABASE-SQL-OPERATIONS.sql
```

## Configuration

| Setting | Source | Required |
|---------|--------|----------|
| `ConnectionStrings:SmartClassSupabase` | User Secrets / env var | Yes |
| `DefaultAdmin:Username` | User Secrets / env var | Yes |
| `DefaultAdmin:Password` | User Secrets / env var | Yes (first seed only) |
| `SchoolTime:TimeZoneId` | `appsettings.json` / env var | Recommended |
| `PasswordReset:*` | User Secrets / env var | No (forgot-password flow) |

Environment variable names use double underscores, for example `ConnectionStrings__SmartClassSupabase`.

Set `SchoolTime:TimeZoneId` to the school's time zone so schedule windows and class-start validation use the correct local date and time. Windows examples include `Singapore Standard Time` and `Tokyo Standard Time`; Linux deployments commonly use `Asia/Singapore` or `Asia/Tokyo`. Leave it empty only when the server's local time zone is intentionally the school time zone.

## Main routes

| Route | Description |
|-------|-------------|
| `/` | Public landing page |
| `/login` | Sign in |
| `/first-access` | Set default admin recovery email (run once) |
| `/forgot-password` | Request a password-reset email |
| `/reset-password` | Complete password reset from email link |
| `/admin` | Administrator dashboard |
| `/teacher` | Teacher dashboard |

## Admin workflow

After signing in as administrator:

1. **Teachers** — create teacher accounts and fingerprint template IDs.
2. **Classrooms** — add rooms with capacity and target temperature.
3. **Schedules** — assign teachers and classrooms; overlapping schedules are rejected.
4. **System settings** — reset all data (`RESET`) or safely remove records (`DELETE`).

Teachers sign in to view their own schedule, profile, attendance, requests, tickets, and assigned classrooms.

## Build and test

```powershell
dotnet build
dotnet run --launch-profile https   # HTTPS on https://localhost:7044
dotnet test .\tests\SmartClassAC.Tests\SmartClassAC.Tests.csproj
```

## Additional documentation

- [PROJECT-IMPROVEMENTS.md](PROJECT-IMPROVEMENTS.md) — prioritized security, business logic, UI, performance, and production-readiness roadmap
- [SUPABASE-SETUP.md](SUPABASE-SETUP.md) — connection strings, SMTP, and step-by-step setup
- [SUPABASE-SQL-OPERATIONS.sql](SUPABASE-SQL-OPERATIONS.sql) — optional DBA queries and emergency operations

## Feature changes

Document new or changed behavior here when you ship work. Add the newest entry at the top.

When a feature touches more than code, also update the related docs in the same change:

| Change type | Update |
|-------------|--------|
| New route or page | **Main routes** (above) and this changelog |
| New config or secret | **Configuration**, `SUPABASE-SETUP.md`, and `scripts/setup-user-secrets.ps1` if prompted at setup |
| Database schema | `SUPABASE-SQL-SETUP.sql`, and `SUPABASE-SQL-OPERATIONS.sql` if admins need new queries |
| Dev workflow | **Running in Cursor IDE**, `.vscode/`, or **Quick start** |

### Changelog

#### 2026-08-31 — Cursor IDE and local setup

- Added `README.md` with setup, routes, and project overview.
- Added `.vscode/launch.json`, `tasks.json`, and `extensions.json` for F5 debugging in Cursor.
- Added `scripts/setup-user-secrets.ps1` to configure Supabase and admin secrets interactively.
- Updated `SUPABASE-SETUP.md` to use repo-relative paths instead of machine-specific paths.

<!-- Template for future entries:
#### YYYY-MM-DD — Short title

- What changed and why.
- Migration or setup steps, if any.
-->

## Security notes

- Keep database and SMTP credentials out of source control; use User Secrets locally and environment variables in production.
- The database connection string is used only by the server — never expose it in browser code.
- Password-reset tokens are stored as SHA-256 hashes and expire after 15 minutes.
