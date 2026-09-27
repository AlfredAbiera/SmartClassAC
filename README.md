# SmartClass AC

SmartClass AC is a **Blazor Server** web application for managing smart classroom air-conditioning. Administrators and teachers manage classrooms, schedules, attendance, leave/overtime requests, support tickets, and temperature commands. All persistent data is stored in **MariaDB** (local XAMPP by default).

Fingerprint terminals (ESP32 + Adafruit sensor) enroll and identify teachers through a **device HTTP API**, using each teacher’s **device PIN** and a numeric sensor template ID.

> **Documentation policy:** This `README.md` is the single source of truth. Update it whenever you change routes, config, schema, device APIs, demo data, deployment, or hardware behavior. Do not add parallel setup guides.

---

## Table of contents

1. [Tech stack](#tech-stack)
2. [Prerequisites](#prerequisites)
3. [Quick start](#quick-start)
4. [MariaDB / XAMPP setup](#mariadb--xampp-setup)
5. [Configuration](#configuration)
6. [Database migrations](#database-migrations)
7. [Device fingerprint API](#device-fingerprint-api)
8. [ESP32 ↔ SmartClass integration plan](#esp32--smartclass-integration-plan)
9. [ESP32 firmware](#esp32-firmware)
10. [Bruno collection](#bruno-collection)
11. [Demo / test data](#demo--test-data)
12. [Deployment](#deployment)
13. [Running in Cursor IDE](#running-in-cursor-ide)
14. [Project structure](#project-structure)
15. [Main routes](#main-routes)
16. [Admin and teacher workflow](#admin-and-teacher-workflow)
17. [Build and test](#build-and-test)
18. [Security notes](#security-notes)
19. [Changelog](#changelog)

---

## Tech stack

| Layer | Technology |
|-------|------------|
| Runtime | .NET 8 (ASP.NET Core) |
| UI | Blazor Server, Razor Pages, Bootstrap 5 |
| Database | MariaDB via MySqlConnector |
| Auth | Custom authentication with PBKDF2 password hashing |
| Email | MailKit (SMTP) for password-reset links |
| Device API | JSON + `X-Device-Api-Key` (fingerprint enroll / scan) |
| Hardware (companion) | ESP32 sketch in `SmartClassAC-ESP32` |

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer
- [XAMPP](https://www.apachefriends.org/) with MariaDB/MySQL running (e.g. `D:\xampp`)
- (Optional) SMTP credentials for the forgot-password flow
- (Optional) [Bruno](https://www.usebruno.com/) to call the device API without the UI
- [Cursor](https://cursor.com) or any editor with **C# Dev Kit** / **C#** — Visual Studio is not required

---

## Quick start

### 1. Clone and restore

```powershell
git clone <repository-url>
cd SmartClassAC
dotnet restore
```

### 2. Create the database and secrets

```powershell
& "D:\xampp\mysql\bin\mysql.exe" -u root -e "CREATE DATABASE IF NOT EXISTS smartclassac CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"
.\scripts\setup-user-secrets.ps1
```

Or set secrets manually (see [MariaDB / XAMPP setup](#mariadb--xampp-setup)).

### 3. Run

```powershell
dotnet run
# or: dotnet watch run
```

Open **http://localhost:5062**. Health: **http://localhost:5062/health**.

### 4. First-time setup

1. App seeds the default administrator on first start.
2. Visit `/first-access` to save the administrator recovery email.
3. Sign in at `/login`.
4. Use **Admin** to create teachers, classrooms, and schedules (or use [demo data](#demo--test-data) in Development).

---

## MariaDB / XAMPP setup

SmartClass stores accounts, classrooms, schedules, attendance, requests, tickets, temperature logs, and fingerprint templates in **MariaDB**. The app uses MySqlConnector and applies schema via versioned migrations on startup.

### Local defaults (setup helper)

| Setting | Default |
|---------|---------|
| Administrator username | `admin` |
| Administrator password | `R3m0vabl3!2028` |
| MariaDB host | `127.0.0.1` |
| MariaDB port | `3306` |
| Database | `smartclassac` |
| Database user | `root` |
| Database password | *(blank — XAMPP default)* |

### Steps

1. Start **XAMPP** → MySQL / MariaDB (`D:\xampp`).
2. Create the database (and optionally apply the initial script):

```powershell
& "D:\xampp\mysql\bin\mysql.exe" -u root -e "CREATE DATABASE IF NOT EXISTS smartclassac CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"
Get-Content .\Migrations\001_initial_schema.sql -Raw | & "D:\xampp\mysql\bin\mysql.exe" -u root smartclassac
```

3. Store credentials in User Secrets (never commit them):

```powershell
.\scripts\setup-user-secrets.ps1
```

Manual equivalent:

```powershell
$project = ".\SmartClassAC.csproj"

dotnet user-secrets init --project $project
dotnet user-secrets set "ConnectionStrings:SmartClassMariaDb" "Server=127.0.0.1;Port=3306;Database=smartclassac;User ID=root;Password=;" --project $project
dotnet user-secrets set "DefaultAdmin:Username" "admin" --project $project
dotnet user-secrets set "DefaultAdmin:Password" "R3m0vabl3!2028" --project $project
dotnet user-secrets set "DeviceApi:ApiKey" "dev-device-key-smartclass" --project $project
```

4. (Optional) Configure SMTP under `PasswordReset:*` for Login → **Forgot password?** (prompted by the setup script, or set via `dotnet user-secrets`).
5. `dotnet run` — migrations run, default admin is seeded; check `/health`.
6. Open `/first-access`, then `/login`.

For a deployed app, set environment variables such as `ConnectionStrings__SmartClassMariaDb`, `DefaultAdmin__Username`, `DefaultAdmin__Password`, and `DeviceApi__ApiKey`.

---

## Configuration

| Setting | Source | Required |
|---------|--------|----------|
| `ConnectionStrings:SmartClassMariaDb` | User Secrets / env | Yes |
| `DefaultAdmin:Username` | User Secrets / env | Yes |
| `DefaultAdmin:Password` | User Secrets / env | Yes (first seed) |
| `DeviceApi:ApiKey` | User Secrets / env | Yes (device fingerprint API) |
| `DemoData:Enabled` | `appsettings.Development.json` | Dev sample teachers/rooms |
| `SchoolTime:TimeZoneId` | `appsettings.json` / env | Recommended |
| `PasswordReset:*` | User Secrets / env | Forgot-password SMTP |

Env vars use double underscores (e.g. `ConnectionStrings__SmartClassMariaDb`).

Set `SchoolTime:TimeZoneId` to the school’s zone so schedule windows **and all recorded timestamps** use that wall clock (`Singapore Standard Time`, `Asia/Singapore`, `Asia/Manila`, etc.). Defaults to `Singapore Standard Time` (UTC+8). Leave empty only to use the server OS local zone.

User-visible records (attendance time-in/out, temperature logs, request/ticket created/reviewed times) are **stored and displayed as school local time** — they are not converted with `ToLocalTime()` / UTC round-trips. Password-reset expiry remains UTC.

| Setting | Default | Meaning |
|---------|---------|---------|
| `SchoolTime:LateGraceMinutes` | `15` | Time-in after schedule start + this many minutes → **Late** |
| `SchoolTime:MissingTimeOutGraceMinutes` | `15` | Open session past schedule end + this many minutes → **Missing time-out** (AC off) |

---

## Database migrations

SQL in [`Migrations/`](Migrations/) is applied in order by `DatabaseMigrationRunner` on startup. Versions are recorded in `schema_versions`.

| File | Purpose |
|------|---------|
| `001_initial_schema.sql` | Core tables (users, classrooms, fingerprints, schedules, attendance, …) |
| `002_fingerprint_numeric_ids.sql` | Numeric fingerprint template IDs for hardware |
| `003_employee_number.sql` | Unique `employee_number` on `user_accounts` |
| `004_device_pin.sql` | Unique `device_pin` (4–6 digits); backfills existing teachers |
| `005_semesters_and_schedule_kinds.sql` | `semesters`, `teacher_semesters`, `class_schedules.semester_id` + `schedule_kind` (Regular/Makeup) |
| `006_biometric_devices.sql` | One fingerprint scanner per classroom (`device_code`); required on enroll/scan |
| `007_attendance_outcomes.sql` | `attendance_logs.outcome` (OnTime / Late / Absent / MissingTimeOut) |

Fresh installs also define `employee_number` / `device_pin` in `001`; later migrations stay safe for older databases.

If a migration fails: fix SQL or add a new numbered file; do not mark a failed version in `schema_versions` until it succeeds.

Useful types: `DatabaseMigrationRunner`, `DatabaseInitializationService` (default admin seed), `MariaDbHealthCheck` (`GET /health`).

---

## Device fingerprint API

Hardware (or Bruno) calls these endpoints with header **`X-Device-Api-Key`** = `DeviceApi:ApiKey`.

| Method | Path | Body | Behavior |
|--------|------|------|----------|
| `POST` | `/api/device/fingerprint/enroll` | `{ "pinCode", "templateId", "deviceCode" }` | Validate room scanner; resolve teacher by PIN; store template |
| `POST` | `/api/device/fingerprint/scan` | `{ "templateId", "deviceCode" }` | Match finger **in that room**; `started` / `ended` / `ac_on` / `ac_off` / `unknown` / `unknown_device` / `wrong_room` / `no_schedule` |
| `POST` | `/api/device/status` | `{ "deviceCode" }` | Power-loss recovery: room AC on/off, active/in-window teacher, `sessionActive` |

Every room has its own ESP32. Shared header `X-Device-Api-Key` authenticates the fleet; **`deviceCode`** (e.g. `ROOM-101`) selects the classroom. Scan only starts a schedule for that classroom; ending must happen on the same room’s scanner.

**Power-loss recovery:** after WiFi connects (boot or reconnect), firmware `1.2.5+` calls `/api/device/status` with its `deviceCode`, restores the relay from `acOn`, and shows `teacherDisplayName` on the LCD when AC is on. Serial `STATUS` also refreshes from the server.

**Attendance vs AC re-entry:** the first in-window scan creates one attendance session (`started` → AC Cooling). A second scan ends it (`ended` → AC Off). Further scans **in the same schedule window** do **not** create another attendance row — they only toggle AC (`ac_on` / `ac_off`). Outside the window, scans return `no_schedule`. When the window ends, reconcile turns Cooling rooms Off if no active session remains.

### Device PIN

- Auto-created when an admin creates a teacher (random 6-digit unless supplied).
- Visible and **editable** on **Teacher → My profile**.
- Used on the terminal for fingerprint enroll/update (ESP32 currently hardcodes `testPinCode` until a keypad is added).
- Demo: Maria **1001**, John **1002**, Ana **1003**.

### Enroll (curl)

```powershell
curl -X POST http://localhost:5062/api/device/fingerprint/enroll `
  -H "Content-Type: application/json" `
  -H "X-Device-Api-Key: your-long-random-key" `
  -d '{ "pinCode": "1001", "templateId": 7, "deviceCode": "ROOM-101" }'
```

Response includes profile fields, assigned `fingerPosition`, `templateId`, and classroom/device info.

### Scan (curl)

```powershell
curl -X POST http://localhost:5062/api/device/fingerprint/scan `
  -H "Content-Type: application/json" `
  -H "X-Device-Api-Key: your-long-random-key" `
  -d '{ "templateId": 12, "deviceCode": "ROOM-101" }'
```

| `action` | Meaning |
|----------|---------|
| `started` | In-window schedule for **this classroom** started |
| `ended` | Active class in **this classroom** closed |
| `unknown` | Template ID not enrolled |
| `unknown_device` | `deviceCode` not registered / inactive |
| `wrong_room` | Teacher has an active class in a different room |
| `no_schedule` | No in-window schedule for this teacher in this room |

Inspect PINs:

```sql
SELECT id, username, employee_number, device_pin FROM user_accounts WHERE role = 'Teacher';
```

---

## ESP32 ↔ SmartClass integration plan

This is the design that was implemented for aligning the ESP32 sketch with SmartClass MariaDB APIs. **As shipped**, enroll uses **device PIN** (`pinCode`) rather than employee number (employee number remains on the teacher profile for HR; PIN is what the terminal uses).

### Problem the plan solved

The previous `.ino` posted form-urlencoded data to a non-SmartClass `:3001/attendance` endpoint, had no enroll flow, toggled AC from a local `isCheckedIn[]` array, and sent no API key / JSON.

### Target flow

```mermaid
sequenceDiagram
  participant User
  participant ESP as ESP32
  participant Sensor as FingerprintSensor
  participant API as SmartClassAPI

  Note over User,API: Enroll
  User->>ESP: PIN (Serial ENROLL / future keypad)
  ESP->>Sensor: Capture and store next free template ID
  Sensor-->>ESP: templateId
  ESP->>API: POST enroll pinCode plus templateId
  API-->>ESP: ok plus teacher profile

  Note over User,API: Usual use
  User->>Sensor: Scan
  Sensor-->>ESP: templateId
  ESP->>API: POST scan templateId
  API-->>ESP: started / ended / ac_on / ac_off / unknown / no_schedule
  ESP->>ESP: AC on if started|ac_on; off if ended|ac_off
```

### Backend (implemented)

| Endpoint | Request body | Notes |
|----------|--------------|-------|
| `POST /api/device/fingerprint/enroll` | `{ "pinCode", "templateId" }` | Resolve teacher by `user_accounts.device_pin`; next free finger slot; device-provided `templateId` (globally unique) |
| `POST /api/device/fingerprint/scan` | `{ "templateId" }` | Unchanged contract: attendance + classroom AC status |

### Firmware (implemented)

1. **Config** — `baseUrl` (e.g. `http://192.168.x.x:5062`), `deviceApiKey`, remove old `:3001/attendance`.
2. **HTTP** — JSON POST, `X-Device-Api-Key`, parse `ok` / `action` from the body.
3. **Enroll** — Serial `ENROLL` (hardcoded `testPinCode` for now): capture twice, store free sensor ID, `POST` enroll, LCD success/fail; delete sensor slot if API fails.
4. **Scan** — On match, `POST` scan; `started`/`ac_on` → AC ON; `ended`/`ac_off` → AC OFF; deny paths do **not** flip AC.
5. **AC** — LCD icons; optional `#define AC_RELAY_PIN -1` for a physical relay later.
6. **Temperature** — DHT for LCD idle display only (optional later: send room temp on scan).

### Out of scope (still)

- Keypad UI for PIN entry (Serial / hardcoded PIN is interim).
- Separate smart-AC cloud protocol beyond classroom status already written on scan start/end.

### Status

| Item | Status |
|------|--------|
| Enroll API (`pinCode` + `templateId`) | Done |
| Scan API + attendance / AC | Done |
| Bruno + README docs | Done |
| ESP32 JSON + API key | Done |
| Serial `ENROLL` + server-trusted scan AC | Done |
| Teacher portal PIN view/edit | Done |
| Keypad on device | Future |

---

## ESP32 firmware

Companion project: `SmartClassAC-ESP32/smartclass/` (`smartclass.ino`).

**Versioning:** bump `FIRMWARE_VERSION` in the sketch on every firmware change (semver). Keep the in-file changelog comment updated. Version is shown on the LCD splash, Serial boot banner, and `STATUS`.

| Constant | Purpose |
|----------|---------|
| `FIRMWARE_VERSION` | Sketch semver (currently `1.2.2`) |
| `ssid` / `password` | Wi-Fi |
| `baseUrl` | PC running SmartClass, e.g. `http://192.168.x.x:5062` |
| `deviceApiKey` | Same as `DeviceApi:ApiKey` |
| `deviceCode` | This room’s scanner ID (e.g. `ROOM-101`) — must match `biometric_devices.device_code` |
| `testPinCode` | Hardcoded enroll PIN until keypad (demo Maria = `1001`) |
| `AC_RELAY_PIN` | GPIO for AC relay, or `-1` disabled |

Serial Monitor **115200**:

| Command | Action |
|---------|--------|
| `HELP` | List commands + firmware version |
| `STATUS` | Firmware version, Wi-Fi, `deviceCode`, sensor template count |
| `ENROLL` | Capture finger → free sensor ID → enroll with `testPinCode` |

Usual use: device boots in **scanning mode** (`Ready to scan`). Place an enrolled finger anytime → local match → `POST /api/device/fingerprint/scan` for attendance logging → AC follows `started` / `ended` / `ac_on` / `ac_off`. While AC is on, the LCD shows **teacher name** on row 1 and **clock · session timer · temp · AC icon** on row 2 (both times use a blinking colon). Overlays return to scanning automatically. `ENROLL` is the only mode that pauses scanning.

Serial Monitor tip (firmware `1.2.6+`): set line ending to **Newline** or **Both NL & CR**. Older builds used blocking `readStringUntil`, which stalled the loop (~1s) whenever USB noise arrived without a newline and made the device look “busy”.

If Serial Monitor shows only □□□ / `` boxes: baud must be **115200** (match `SERIAL_BAUD`). Also check fingerprint wiring — sensor **TX → GPIO16**, **RX → GPIO17**; never onto GPIO1/3 (USB). Tools → board option **USB CDC On Boot = Disabled** for classic ESP32-WROOM USB-UART boards. Flash `1.2.8+`, press reset, and you should see an ASCII `SmartClass device booting` banner first.

Templates live on the **physical sensor**. Demo DB fingerprint IDs alone will not match until you run `ENROLL` on the ESP32 (stores on sensor + POSTs to the API).

---

## Bruno collection

Folder: [`bruno/`](bruno/). Open as a collection in [Bruno](https://www.usebruno.com/).

1. Start the app (`http://localhost:5062`).
2. Select environment **Local** (`environments/Local.json`).
3. Set `deviceApiKey` = User Secrets `DeviceApi:ApiKey` (local default often `dev-device-key-smartclass`).
4. Set `pinCode` / `templateId` as needed.

| Request | Purpose |
|---------|---------|
| Health | `GET /health` |
| Enroll fingerprint | `POST …/enroll` (`pinCode` + `templateId`) |
| Scan fingerprint | `POST …/scan` |
| Scan without API key | Expect `401` |

---

## Demo / test data

Controlled by `DemoData:Enabled` in `appsettings.Development.json` (default **true**). Production `appsettings.json` keeps it **false**.

On startup, if employee `EMP-1001` is missing, the app seeds teachers, classrooms, schedules, and Maria’s fingerprints **1** and **2**. Demo PINs are enforced as **1001** / **1002** / **1003**.

To re-seed: Admin → **Reset all system data**, then restart in Development.

### Logins

| Role | Username | Password | Notes |
|------|----------|----------|-------|
| Admin | `DefaultAdmin:Username` (often `admin`) | User Secrets password | Not part of demo seed |
| Teacher | `msantos` | `TeacherDemo1!` | EMP-1001, PIN **1001**, fingerprints **1**, **2** |
| Teacher | `jreyes` | `TeacherDemo1!` | EMP-1002, PIN **1002** |
| Teacher | `acruz` | `TeacherDemo1!` | EMP-1003, PIN **1003**, **no schedules** |

Shared teacher password meets app policy (≥10 chars, upper, lower, digit).

### Classrooms

| Name | Capacity | Target °C | Typical use |
|------|----------|-----------|-------------|
| Room 101 | 40 | 24 | Maria in-window + tomorrow math |
| Room 202 | 35 | 22 | John in-window + yesterday English |
| Science Lab | 28 | 20 | Maria afternoon science |
| Computer Lab | 30 | 23 | Free room / AC tests |

### Teachers & schedules

| Employee # | PIN | Username | Fingerprints | Schedules |
|------------|-----|----------|--------------|-----------|
| EMP-1001 | 1001 | `msantos` | **1**, **2** | Today in-window Math (Room 101); tomorrow Math; today afternoon Science Lab |
| EMP-1002 | 1002 | `jreyes` | none | Today in-window English (Room 202); yesterday English |
| EMP-1003 | 1003 | `acruz` | none | **None** |

In-window start/end at seed time ≈ **now − 1h** through **now + 2h** so fingerprint **start class** works the day you seed.

### Suggested test cases

1. Admin login — teachers/rooms lists show demo rows.
2. Teacher `msantos` — profile shows EMP-1001, PIN **1001**, fingerprints ≥ 2.
3. `acruz` — no in-window schedule / scan → `no_schedule` after enroll.
4. Schedule conflict — overlapping Math for Maria in Room 101 → rejected.
5. Enroll — `{ "pinCode": "1003", "templateId": 10 }`.
6. Scan `1` in Maria’s window → `started`; scan again → `ended`; scan again while still in window → `ac_on` (AC only).
7. Scan `9999` → `unknown`.
8. Admin AC target on Computer Lab → temperature log row.
9. Leave + substitute (Maria → John) — watch schedule overlap.

---

## Deployment

### First-time / development

```powershell
& "D:\xampp\mysql\bin\mysql.exe" -u root -e "CREATE DATABASE IF NOT EXISTS smartclassac CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"
# Optional: apply 001 manually; otherwise startup migrations apply all
dotnet run --launch-profile http
```

### Production

Prefer applying schema (or relying on `DatabaseMigrationRunner`) with a least-privilege MariaDB user, then start the app. Required env:

| Key | Example |
|-----|---------|
| `ConnectionStrings__SmartClassMariaDb` | `Server=…;Database=smartclassac;User ID=…;Password=…;` |
| `DefaultAdmin__Username` | `admin` |
| `DefaultAdmin__Password` | *(strong)* |
| `DeviceApi__ApiKey` | *(long random)* |
| `PasswordReset__*` | SMTP when needed |
| `SchoolTime__TimeZoneId` | e.g. `Singapore Standard Time` |

Monitor `GET /health` from the load balancer. Never commit secrets. Use managed MariaDB with backups.

Example deploy sketch:

```powershell
# Apply Migrations/*.sql (mysql client or startup runner), then:
dotnet run --configuration Release
```

---

## Running in Cursor IDE

1. Open the `SmartClassAC` folder in Cursor.
2. Install recommended extensions (**C# Dev Kit**, **C#**).
3. Run via **F5** (`SmartClassAC (http)` / `https`), `dotnet watch run`, or `dotnet run`.
4. Optional: `dotnet dev-certs https --trust`.

Profiles: HTTP **5062**, HTTPS **7044** (`.vscode/launch.json` / `Properties/launchSettings.json`).

**LAN / ESP32 access:** Kestrel binds `http://0.0.0.0:5062` so devices on Wi‑Fi can reach the PC. Browse the UI at **http://localhost:5062**. Set the ESP32 `baseUrl` to `http://<your-PC-LAN-IP>:5062` (from `ipconfig`). Allow port **5062** in Windows Firewall if prompted. Binding only `localhost` makes the ESP show `No reach PC` even when Wi‑Fi is connected.

---

## Project structure

```
SmartClassAC/
├── .vscode/              # Launch and build tasks
├── scripts/              # setup-user-secrets.ps1
├── Migrations/           # Versioned MariaDB SQL (001–004+)
├── bruno/                # Device API HTTP collection + Local env
├── Pages/                # Blazor pages
├── Shared/               # Layout / navigation
├── Services/             # Auth, MariaDB, migrations, device API, demo seed
├── wwwroot/
├── Properties/
├── Program.cs            # Entry + device routes
├── appsettings*.json
└── README.md             # ← canonical docs (only markdown in this repo)
```

Companion hardware: `SmartClassAC-ESP32/smartclass/`.

---

## Main routes

| Route | Description |
|-------|-------------|
| `/` | Landing |
| `/login` | Sign in |
| `/first-access` | Admin recovery email (once) |
| `/forgot-password` / `/reset-password` | Password reset |
| `/admin` | Administrator dashboard |
| `/teacher` | Teacher dashboard (editable **device PIN**) |
| `/health` | MariaDB health |
| `POST /api/device/fingerprint/enroll` | Enroll by PIN + template ID |
| `POST /api/device/fingerprint/scan` | Identify → start/end class |

---

## Admin and teacher workflow

**Admin:** create/edit teachers (name, employee number, username, email, optional device PIN and password), **semesters** (enroll teachers), classrooms, schedules with **Regular/Makeup** kind (teacher must be enrolled; overlaps rejected); system reset (`RESET`) / delete.

**Teacher:** schedule (shows semester + kind), profile (PIN), attendance, requests, tickets, assigned rooms. Enroll fingerprints on the terminal with the PIN; daily start/end via scan when a schedule window is active in an **active semester**.

### Required domain flow (status)

Target chain (SMS deferred):

```
TEACHER
   ↓
TEACHER_SEMESTER
   ↓
REGULAR_SCHEDULE / MAKEUP_CLASS
   ↓
CLASSROOM
   ├── BIOMETRIC_DEVICE
   └── AC_UNIT
          ↓
BIOMETRIC_EVENT
          ↓
ATTENDANCE
   ├── LATE
   ├── EARLY DISMISSAL
   ├── ABSENT
   ├── ON LEAVE
   ├── MISSING TIME-OUT
   └── OVERTIME MINUTES
          ↓
ATTENDANCE_EXCEPTION
          ↓
NOTIFICATION / SMS

ATTENDANCE
      ↓
AC_EVENT
      ↓
AC ON / AC OFF
```

| Step | Status | Notes |
|------|--------|-------|
| **TEACHER** | Done | Create/edit; employee #; device PIN; fingerprints |
| **TEACHER_SEMESTER** | Done | Semesters + enroll/unenroll; schedules require enrollment |
| **REGULAR / MAKEUP** | Done | `schedule_kind`; Makeup preferred when overlapping |
| **CLASSROOM** | Done | Rooms, capacity, target temperature |
| **BIOMETRIC_DEVICE** | Done | Per-room `deviceCode`; required on enroll/scan |
| **AC_UNIT** | Partial | Room `ac_status` only — no separate AC inventory table |
| **BIOMETRIC_EVENT** | Done | ESP32 / API enroll + scan → start/end class |
| **ATTENDANCE** session | Done | `Active` → `Completed`; school-local timestamps |
| **LATE** | Done | Time-in after `LateGraceMinutes` |
| **ABSENT** | Done | Schedule ended with no punch |
| **MISSING TIME-OUT** | Done | Open past end + grace → auto-close, AC off |
| **ON TIME** | Done | Stored as `OnTime` (extra vs original list) |
| **EARLY DISMISSAL** | Not yet | `EarlyOut` request type exists; not derived from punches |
| **OVERTIME MINUTES** | Not yet | `Overtime` request type exists; not derived from punches |
| **ON LEAVE** | Partial | Leave requests exist; not auto-linked to attendance outcome |
| **AC ON / OFF** | Done | Class start → Cooling; end / missing timeout → Off (+ temperature logs) |
| **ATTENDANCE_EXCEPTION** | Not yet | No exception table / workflow |
| **NOTIFICATION / SMS** | Not yet | Deferred by design |

**Live spine today:** Teacher → semester → Regular/Makeup schedule → room scanner → punch → attendance (On time / Late / Absent / Missing time-out) → AC on/off.

**Natural next:** early dismissal / overtime from punches → wire leave → On Leave → exceptions → SMS.

### Semesters and schedule kinds

```
Teacher → TeacherSemester → RegularSchedule / MakeupClass → Classroom
```

- **Semesters** — Admin → Semesters: create date-ranged terms, activate/deactivate, enroll teachers.
- **Enrollment** — required in `teacher_semesters` before creating schedules for that semester.
- **Schedule kind** — `Regular` or `Makeup`; date must fall inside the semester; when both are in window, **Makeup is preferred** for biometric/dashboard start.
- Demo seed creates “Demo Semester {year}”, enrolls all demo teachers, and marks Science Lab as Makeup.

### Attendance outcomes

```
BiometricEvent → Attendance → Late | Absent | MissingTimeOut | OnTime
```

| Outcome | When |
|---------|------|
| **On time** | Time-in within `LateGraceMinutes` after schedule start; normal time-out |
| **Late** | Time-in after the late grace, still inside the schedule window |
| **Missing time-out** | Session still open after schedule end + `MissingTimeOutGraceMinutes` (auto-closed; AC off) |
| **Absent** | Schedule ended with no time-in punch (system row) |

Session lifecycle remains `Active` → `Completed` (**one attendance session per schedule**). After time-out, further in-window scans only toggle AC (`ac_on` / `ac_off`) and write temperature-log events `AcReentryOn` / `AcReentryOff`. Reconciliation runs every minute and also before scan / dashboard attendance reads; it also turns AC Off when a room is Cooling with no active session and no schedule still in window.

---

## Build and test

```powershell
dotnet build
dotnet run --launch-profile https
dotnet test .\tests\SmartClassAC.Tests\SmartClassAC.Tests.csproj
```

Smoke the device API with Bruno (`pinCode` **1001**, etc.).

---

## Security notes

- Keep DB, device API key, and SMTP out of source control (User Secrets / env).
- Connection string stays on the server only.
- Require `X-Device-Api-Key` on device routes.
- Device PINs are short shared secrets — rotatable from the teacher portal; do not reuse admin passwords.
- Password-reset tokens are SHA-256 hashed and expire after 15 minutes.

---

## Changelog

Document every shipped change here (newest first). Also update the relevant sections of this README in the **same** change.

| Change type | Update in this README |
|-------------|------------------------|
| Route / page | [Main routes](#main-routes) + changelog |
| Config / secret | [Configuration](#configuration) + setup script if prompted |
| Schema | [Database migrations](#database-migrations) + changelog |
| Device / hardware | [Device fingerprint API](#device-fingerprint-api), [ESP32](#esp32-firmware), Bruno, demo data |
| Deploy | [Deployment](#deployment) |
| Demo seed | [Demo / test data](#demo--test-data) |

### Entries

#### 2026-09-27 — Device status recovery after power loss

- `POST /api/device/status` `{ deviceCode }` returns room `acOn` / `acStatus`, `teacherDisplayName`, and `sessionActive`.
- ESP32 firmware `1.2.5` calls it after WiFi connect/reconnect (and Serial `STATUS`) to restore relay + LCD teacher name.

#### 2026-09-27 — LCD shows logged-in teacher while AC is on

- ESP32 firmware `1.2.4`: after `started` / `ac_on`, scanning idle screen shows teacher name + `AC ON`; cleared on `ended` / `ac_off`.

#### 2026-09-27 — In-window AC re-entry (no new attendance)

- After time-out for a schedule, further scans while still inside that schedule window toggle AC only (`ac_on` / `ac_off`); attendance is not started again.
- ESP32 firmware `1.2.3` shows “AC resumed” / “AC off” for those actions.
- Reconcile turns Cooling Off when the schedule window ends and no active session remains.

#### 2026-09-27 — ESP32 firmware versioning

- Sketch tracks `FIRMWARE_VERSION` (semver) with in-file changelog; bump on every firmware change.
- Version shown on LCD splash, Serial boot, `HELP`, and `STATUS` (current `1.2.3`).
- App listens on `0.0.0.0:5062` so LAN scanners can reach the PC (not localhost-only).

#### 2026-09-27 — Document required workflow status

- README now maps the full Teacher → … → Attendance → AC / SMS domain flow with Done / Partial / Not yet statuses.

#### 2026-09-27 — School-local recorded timestamps

- Attendance, temperature logs, requests, and tickets now write and display school wall-clock time via `SchoolTime:TimeZoneId` (default `Singapore Standard Time`).
- Removed `ToLocalTime()` display conversions that shifted already-local / mislabeled UTC values.

#### 2026-09-27 — Attendance outcomes (Late / Absent / Missing time-out)

- Migration `007`: `attendance_logs.outcome` (`OnTime`, `Late`, `Absent`, `MissingTimeOut`).
- Time-in after late grace → Late; ended schedule with no punch → Absent; open session past end + grace → Missing time-out (AC off).
- Background reconciliation every minute; also on scan and attendance list loads.
- Admin/Teacher attendance tables show outcome badges.

#### 2026-09-27 — Per-room biometric devices

- Migration `006`: `biometric_devices` (one scanner per classroom, unique `device_code`).
- Enroll/scan require `deviceCode`; scan binds schedule/AC to that room (`wrong_room` / `unknown_device`).
- New classrooms auto-create a scanner code from the room name; Admin classrooms show the code.
- ESP32 / Bruno send `deviceCode` on every request.

#### 2026-09-27 — Semesters + Regular/Makeup schedules

- Migration `005`: `semesters`, `teacher_semesters`, `class_schedules.semester_id` / `schedule_kind`.
- Admin Semesters page (create, activate, enroll/unenroll); schedule form requires semester + kind.
- Biometric/dashboard class start only matches schedules in an active semester with teacher enrollment; Makeup preferred when overlapping.
- Demo seed creates a demo semester and enrolls teachers.

#### 2026-09-27 — Consolidate documentation into README

- Merged MariaDB setup, deployment, demo/test data, Bruno, ESP32 device notes, and the fingerprint integration plan into this file.
- Declared `README.md` the single source of truth; removed other project `.md` guides.

#### 2026-09-27 — MariaDB-only cleanup

- Removed obsolete cloud-database docs and unused alternate repository/health-check code.
- UI copy refers to MariaDB only.

#### 2026-09-27 — Device PIN + ESP32 fingerprint integration

- Migrations `002`–`004`; teacher auto PIN; portal edit.
- Enroll `{ pinCode, templateId }`; scan drives attendance + AC.
- Demo PINs 1001/1002/1003; Bruno collection; ESP32 JSON + Serial `ENROLL`.

#### 2026-08-31 — Cursor IDE and local setup

- Initial README, `.vscode` launch/tasks, `scripts/setup-user-secrets.ps1`.

<!-- Template:
#### YYYY-MM-DD — Short title

- What changed and why.
- Migration or setup steps, if any.
-->
