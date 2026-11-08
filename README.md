# SmartClass AC

SmartClass AC is a **Blazor Server** web application for managing smart classroom air-conditioning. Administrators and teachers manage classrooms, schedules, attendance, and temperature commands. All persistent data is stored in **MariaDB** (local XAMPP by default).

Fingerprint terminals (ESP32 + Adafruit sensor) identify teachers through a **device HTTP API** using numeric sensor template IDs. Legacy device enrollment is disabled while a safer replacement workflow is developed.

> **Documentation policy:** This `README.md` is the single source of truth. Update it whenever you change routes, config, schema, device APIs, demo data, deployment, or hardware behavior. Do not add parallel setup guides.

---

## Table of contents

Detailed guides: [Portal process flow](docs/PORTAL_PROCESS_FLOW.md) ·
[Firmware process flow](docs/FIRMWARE_PROCESS_FLOW.md) ·
[Data model and relationships](docs/DATA_MODEL.md).

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

SmartClass stores accounts, classrooms, schedules, attendance, temperature logs, and fingerprint templates in **MariaDB**. The app uses MySqlConnector and applies schema via versioned migrations on startup.

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

### Optional device features: Development PowerShell commands

Run these commands in PowerShell from the repository root (`D:\SmartClassAC`).
They configure **Development User Secrets for this Windows user**; they do not
change another user's settings or a deployed portal's configuration. Enable
only the features you intend to use. Restart the portal after changing settings
and check that migrations have completed; a running process or environment
variable can use different values.

The authenticated features below also require the fleet API key, a stable
`EnrollmentSecurity:MasterKey`, and an individually provisioned scanner key.
Faculty OTP additionally requires its own stable `FacultyOtp:Secret` and a
configured GSM modem. See the enrollment and Faculty OTP sections below before
enabling them. Do **not** overwrite or regenerate existing secrets when
enabling flags: changing the master key can make provisioned scanner keys
unavailable, and changing the OTP secret invalidates outstanding codes.

**Faculty login, schedule menu, and scanner temperature target** (each line
enables the indicated feature; the menu and target also require login):

```powershell
dotnet user-secrets set "FacultyOtp:EnableDeviceApi" "true" --project .\SmartClassAC.csproj
dotnet user-secrets set "FacultyOtp:EnableMenuApi" "true" --project .\SmartClassAC.csproj
dotnet user-secrets set "FacultyOtp:EnableAcTargetApi" "true" --project .\SmartClassAC.csproj
```

**AC IR and attendance power toggle:**

```powershell
dotnet user-secrets set "AcIr:EnableDeviceApi" "true" --project .\SmartClassAC.csproj
dotnet user-secrets set "AcIr:EnablePowerToggle" "true" --project .\SmartClassAC.csproj
```

The power flag is global and disables the legacy IR routes. Upgrade **all**
scanners that use IR to power-capable firmware first; source 1.6.60 defaults
to the v2 protocol. A v2 scanner cannot synchronize IR until both portal flags
are enabled. Apply migration `042`, restart the portal and scanner, and have an
admin physically verify the AC target and ON/OFF state in **AC control →
Reconcile**. A toggle may be sent after confirmation. A transmitted signal
does not prove the AC received it.

**Experimental fingerprint enrollment and inventory:** enable these only for a
deliberately validated Development deployment with compatible firmware and the
enrollment safeguards described below. Enabling the API does not itself enable
firmware writes; scanner source 1.6.61 enables guarded enrollment writes by
default.

```powershell
dotnet user-secrets set "EnrollmentSecurity:EnableOperationApi" "true" --project .\SmartClassAC.csproj
dotnet user-secrets set "EnrollmentSecurity:EnableClaimApi" "true" --project .\SmartClassAC.csproj
dotnet user-secrets set "EnrollmentSecurity:EnableFinishApi" "true" --project .\SmartClassAC.csproj
dotnet user-secrets set "EnrollmentSecurity:EnableRecoveryApi" "true" --project .\SmartClassAC.csproj
dotnet user-secrets set "EnrollmentSecurity:EnableInventoryApi" "true" --project .\SmartClassAC.csproj
```

To disable a flag, repeat its command with `"false"` instead of `"true"`.
Changing the IR power flag also requires restarting the scanner and physically
reconciling again; reverting to legacy mode requires legacy-compatible scanner
firmware. Do not enable experimental replacement endpoints merely by analogy;
their separate gates and incomplete recovery limitations are described below.
For non-Development deployments, configure the equivalent names using `__`
instead of `:` (for example `AcIr__EnablePowerToggle`) through the deployment's
protected configuration. Outside Development, device APIs require HTTPS.

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

User-visible records (attendance time-in/out and temperature logs) are **stored and displayed as school local time** — they are not converted with `ToLocalTime()` / UTC round-trips. Password-reset expiry remains UTC.

### Attendance reports

Administrators can open **Operations → Attendance → Create attendance report**, choose an inclusive date range and optional teacher, classroom, and outcome, then select **Print / PDF** or **Download Excel**. Use the browser's print dialog to save a PDF. Both outputs include all matching records and outcome totals, not just the current attendance-table page. The report date is the scheduled class date when a schedule exists, otherwise the school-local time-in date; this ensures a late-created absence belongs to its class date. Absent records have no punches, and automatically closed missing time-outs are not shown as actual time-out punches. Active sessions, including active late sessions, are counted under Active; the detail still identifies active late sessions. Other covers completed records without a recognized outcome. Report generation reconciles attendance first and both output routes require an active administrator account.

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
| `006_biometric_devices.sql` | Legacy one-scanner-per-classroom registration; IDs later migrated in `012` |
| `007_attendance_outcomes.sql` | `attendance_logs.outcome` (OnTime / Late / Absent / MissingTimeOut) |
| `008_teacher_mobile_phone.sql` | Unique `phone` on teachers; backfills missing mobiles |
| `009_pending_clear_fingerprints.sql` | `biometric_devices.pending_clear_fingerprints` for Admin remote sensor wipe |
| `010_biometric_device_settings.sql` | Per-device status polling interval and fingerprint scan/enrollment controls |
| `011_unassigned_biometric_devices.sql` | Optional classroom assignment and stable `Device {id}` display names |
| `012_generated_device_ids.sql` | Rename room-derived device codes to generated `DEVICE-{id}` identifiers |
| `013_admin_device_pin.sql` | Hashed administrator PIN used for on-device admin options |
| `014_numeric_device_ids_pairing.sql` | Numeric device IDs and one-time pairing codes |
| `015_enrollment_scanner.sql` | One administrator-selected enrollment scanner; synchronization remains unavailable |
| `016_fingerprint_slot_blocks.sql` | Blocks selected legacy fingerprint slots from attendance matching |
| `017_fingerprint_enrollment_preparations.sql` | Records preparation-only slot reservations and keeps their attendance blocks in place |
| `018_enrollment_auth_challenges.sql` | Stores hashes of short-lived, single-use enrollment authentication challenges |
| `019_fingerprint_write_guards.sql` | Persists unresolved fingerprint-write guards and protects their preparations |
| `020_fingerprint_write_completion.sql` | Adds scanner-bound mappings and durable completion receipts |
| `021_enrollment_device_credentials.sql` | Encrypted device-key storage and master-key verification for future enrollment |
| `022_enrollment_completion_slot.sql` | Records the slot in completion receipts |
| `023_enrollment_authorizations.sql` | Short-lived administrator consent for enrollment |
| `024_fingerprint_sensor_inventory.sql` | Stores authenticated scanner inventory snapshots |
| `025_fingerprint_replacement_preparations.sql` | Identifies replacement reservations without enabling writes |
| `026_fingerprint_replacement_recovery_intents.sql` | Records guarded replacement recovery intents without enabling writes |
| `027_fingerprint_recovery_attempts.sql` | Keeps one unresolved recovery attempt bound to its write guard |
| `028_fingerprint_replacement_completions.sql` | Records replacement completion receipts |
| `029_fingerprint_replacement_write_claims.sql` | Retains replacement consent and one-use claims |
| `030_fingerprint_replacement_write_fences.sql` | Retires the original replacement write path |
| `031_fingerprint_recovery_write_claims.sql` | Records one-use recovery write claims |
| `032_fingerprint_recovery_completions.sql` | Records recovery completion receipts |
| `033_fingerprint_recovery_attempt_fences.sql` | Retires individual recovery attempts without granting a retry |
| `034_fingerprint_recovery_intent_history.sql` | Retains issued recovery identities while their guard is unresolved |
| `040_buzzer_settings.sql` | Global schedule-ending buzzer reminder settings |
| `041_buzzer_test_requests.sql` | Short-lived, per-device buzzer tests |

Fresh installs also define `employee_number` / `device_pin` / unique `phone` in `001`; later migrations stay safe for older databases.

### Schedule-ending buzzer

**System → Buzzer settings** lets administrators enable reminders and set the first reminder and repeat interval (1–180 minutes each). Migration `040` initializes the requested enabled, 15-minute lead and 5-minute repeat. Starting at the lead time, a connected scanner plays a five-second melody for an active attendance session in its assigned room, repeating at the configured interval strictly before the scheduled end. Firmware before `1.6.56` plays three short tones instead. A class that ends early stops producing reminders. The scanner checks the portal approximately every five seconds using school-local schedule time; network delays, disconnected scanners, blocking device operations, and a 20-second reminder window can cause reminders to be missed. Missed reminders are not replayed; a scanner reboot during a reminder window may repeat that reminder.

Administrators can select an active, paired device and use **Test buzzer** to queue the same melody, even when reminders are disabled or no class is active. The test expires after 30 seconds, is claimed at most once, and is invalidated by re-pairing. A queued or claimed request does not confirm the sound played; listen at the device. An offline scanner, an older firmware version, network errors, or a busy scanner can cause the test to be missed.

Firmware source `1.6.57` expects a **passive buzzer on GPIO13**. Do not connect a low-impedance speaker directly to the ESP32 GPIO; use a suitable audio amplifier. Confirm electrical compatibility and use a suitable driver if a buzzer draws more current than the ESP32 GPIO can safely supply. The portal and firmware must both be updated, migrations `040` and `041` applied, and the sketch built and uploaded before testing. The buzzer endpoint requires the fleet device API key and HTTPS outside Development; Development HTTP is for a trusted LAN only. Physical sound and timing require hardware testing.

If a migration fails: fix SQL or add a new numbered file; do not mark a failed version in `schema_versions` until it succeeds.

Useful types: `DatabaseMigrationRunner`, `DatabaseInitializationService` (default admin seed), `MariaDbHealthCheck` (`GET /health`).

---

## Device fingerprint API

Hardware (or Bruno) calls these endpoints with header **`X-Device-Api-Key`** = `DeviceApi:ApiKey`.

| Method | Path | Body | Behavior |
|--------|------|------|----------|
| `POST` | `/api/device/fingerprint/enroll` | `{ "pinCode", "templateId", "deviceId" }` | Disabled; returns HTTP 503 for a recognized device |
| `POST` | `/api/device/fingerprint/scan` | `{ "templateId", "deviceId" }` | Match finger **in the device's assigned room**; `started` / `ended` / `ac_on` / `ac_off` / `unknown` / `unknown_device` / `wrong_room` / `no_schedule` / `disabled` |
| `POST` | `/api/device/status` | `{ "deviceId" }` | Room AC/teacher snapshot (or safe off/unassigned state), device display/target temperature/polling/features, remote AC sync, and pending sensor wipe |
| `POST` | `/api/device/clear-session` | `{ "deviceId" }` | End active attendance in an assigned room + set AC Off; clears pending fingerprint wipe (used by Serial `DELETEALL` / remote clear) |
| `POST` | `/api/device/admin/pair` | `{ "adminPin", "pairingCode" }` | Verify the admin device PIN and a one-time pairing code, then return the device ID and display name. `401` invalid PIN/code or device API key; `429` locked out |
| `POST` | `/api/device/admin/rooms` | `{ "deviceId", "adminPin" }` | Verify the admin device PIN; return unassigned active rooms plus this device's current room (`rooms`, compact `roomList`). `401` wrong PIN, `429` locked out (5 failures → 5 min, per device) |
| `POST` | `/api/device/admin/assign` | `{ "deviceId", "adminPin", "classroomId" }` | Re-verify the PIN, then assign the device to a free room, or unassign when `classroomId` is `null`. `409` if the room was taken or a class session is active |

**On-device room assignment (firmware `1.5.0+`):** an admin sets a 6-digit **Device admin PIN** in **Admin → System settings** (stored hashed, migration `013`). On the scanner press `#` → Admin → enter the PIN (checked by the server, never stored on the device) → **Assign room** to pick from free rooms (current room marked `*`) or **Unassign**. Until a PIN is set, device admin options stay locked.

**Scanner navigation (firmware `1.6.2+`):** press `#` on the ready screen to open the menu. In menu lists, press `*` for previous, `#` for next, and tap `0` to select; hold `0` for one second to go back. In PIN, ID, and pairing-code entry, digits enter the value, `*` deletes a digit (or goes back when empty), and `#` submits. Firmware `1.6.3+` uses a stationary text menu; unsupported characters in room names appear as `?` on the scanner.

Firmware `1.6.45+` displays the matched teacher's server-provided name with successful scan results, including class end and AC-off, and for a recognized teacher denied because of the room, schedule, or a server error. Unknown or unidentified scans do not show a name. The 16-character LCD truncates longer names and shows unsupported characters as `?`. Building this firmware requires the ArduinoJson library (tested with version 7.4.3).

Starting with firmware `1.6.46`, recognized scans show the teacher's name first for at least two seconds, then the attendance, AC, or denial result. Scans without a server-provided teacher name show the result directly.

Shared header `X-Device-Api-Key` authenticates the fleet; **`deviceId`** is generated by the server as a long numeric ID (`1000000001`, `1000000002`, …) and remains the same regardless of classroom assignment. For **pairing**, first save a 6-digit Device admin PIN in Admin → System settings. This is distinct from the one-time 6-digit pairing code shown in the device table. On an unpaired scanner, press `#` → Admin → enter the admin PIN → enter the pairing code when prompted. On an already paired scanner, enter the admin PIN, then choose **Pair device** and enter the code. The registered device must be active. A successful response returns `{ ok, deviceId, deviceDisplayName }` and the scanner saves the ID in flash; unpaired IDs are rejected by other device endpoints. Migration `014` renumbers existing devices, which must be re-paired (firmware `1.6.0+`). Admin-managed display names (`Device 1`, `Device 2`, …) are also generated and cannot be edited. A device can remain unassigned while being provisioned, but attendance scanning and fingerprint enrollment are rejected until an admin assigns it to a classroom.

Firmware `1.6.1+` distinguishes invalid PIN/code, an invalid device API key, server errors, and lockout on the LCD. Five failed pairing attempts in one minute cause a five-minute lockout; generating another code does not clear it. If the code was consumed but the scanner did not save its ID, generate a new code. Do not share PINs, pairing codes, or API keys when reporting an error.

**Startup settings and power-loss recovery:** after Wi-Fi connects (boot or reconnect), firmware calls `/api/device/status` with its `deviceId`, restores the relay from `acOn`, and receives the admin-managed device name, assigned room, room target temperature, polling interval, and scan/enrollment controls. It also polls status at the configured interval; `/health` remains on its own 30-second check. Serial `STATUS` refreshes the same settings. Wi-Fi credentials, server URL, and API key stay provisioned on the device because they are required to connect and authenticate before it can download settings.

**Admin → Devices:** search by device name, ID, room, or status, and filter by activity, assignment, pairing, scan, or enrollment state. Register devices and manage their room assignment (including disassociation), room target temperature, status polling interval (5–3600 seconds), active state, attendance scanning, and fingerprint enrollment. Names are generated from each device's stable database ID and cannot be edited. A room can have one scanner; unassigned devices are allowed. Moving/disassociating/deactivating a scanner or disabling attendance scans is blocked while an affected room has an open class session. The scan/enrollment controls are enforced by the server as well as downloaded to firmware. Target temperature is sent as the room setpoint; firmware displays it, while actual AC control remains relay on/off.

The enrollment scanner selector records the administrator's selected scanner. The portal can reserve a slot and record short-lived consent; firmware 1.6.21 adds an admin keypad entry to the experimental enrollment flow, which remains compile-time disabled by default. An opt-in 1.6.20 build completed one physical enrollment and attendance scan; the keypad entry in 1.6.21 still requires hardware validation. Template synchronization is not available. The legacy enrollment API remains disabled.

Firmware 1.6.7 contains an unconnected, bounded two-impression capture routine that builds a model only in sensor memory. It does not store a model in a sensor slot, communicate with the server, or enable enrollment. It has not been tested on the physical scanner and must not be used as evidence that enrollment or recovery works.

Firmware 1.6.8 introduced the persistent write journal. In current firmware, the enrollment flow uses it to record uncertainty before storing a model and confirmation only after a checked sensor acknowledgement. A journal that is present, corrupt, or inaccessible blocks firmware sensor-clear commands and re-pairing. Current firmware also uses authenticated server recovery and finish receipts. Existing older firmware does not have these protections.

A read-only diagnostic on the current JM-101B sensor reported capacity 300 and returned an index-page acknowledgement with first bitmap byte `0x04` on three boots, consistent with the previously enrolled diagnostic slot 2. This observation does not establish empty-slot semantics or overwrite behavior and is not authorization to write. The diagnostic temporarily replaces the normal ESP32 application; restore application firmware only after an approved, safe deployment plan.

The installed Adafruit fingerprint library's packet reader does not validate acknowledgement checksums. Its `storeModel` result therefore is not sufficient proof for journal confirmation. Current firmware uses a sensor protocol adapter that validates acknowledgement type, address, length, checksum, and timeout; a communication fault poisons the adapter session. Host simulations cover malformed and incomplete write responses. A separate read-only diagnostic using this adapter successfully verified the sensor and read capacity 300 and its index on the physical scanner.

Firmware 1.6.10 introduced a checked slot-index inspection helper with capacity and bitmap-page bounds checks. Current firmware uses a sensor check before enrollment; an index result does not authorize an overwrite or prove the identity of a stored fingerprint.

Administrators can use **Management → Teachers → Manage fingerprints** on a teacher's row to view that teacher's server mappings and automatically reserve a slot for a selected finger. This records an active teacher, finger, and selected active, paired scanner assigned to an active room, and blocks the slot from attendance matching in the same transaction. The scanner must have enrollment enabled and no pending sensor clear. The server rejects new preparations for teachers with two or more fingerprints and keeps existing mappings intact. A preparation does not grant permission to overwrite a sensor model or initiate capture. Only one preparation per scanner, slot, or teacher/finger is allowed. Same-slot replacement is separately gated.

Migration `024` and firmware `1.6.22` add an experimental, read-only inventory prerequisite for automatic slot selection. When the firmware is compiled with enrollment writes enabled and the independent `EnrollmentSecurity:EnableInventoryApi` flag is enabled, an idle paired scanner periodically reads its capacity and index bitmap and sends an authenticated, challenge-bound snapshot. The server stores only the latest accepted snapshot. Inventory is disabled by default, supports capacities from 2 through 2048, and requires the per-device enrollment key and the same Development-only HTTP policy as the enrollment routes. The teacher fingerprint dialog automatically reserves a sensor-empty, globally unmapped and unblocked slot from a snapshot whose challenge was issued within 90 seconds. If no recent inventory is available, check that the scanner is online and retry after it refreshes. Snapshots and outstanding inventory challenges are invalidated when writes are guarded or finished and when sensor clearing is requested or reported. Reports are rejected while a write is guarded or a clear is pending. Even a recent snapshot is not write permission: the scanner checks the slot again before capture and refuses an occupied slot.

On the development scanner `1000000005`, a hardware report was received with capacity 300 and the index bits for slots 2 and 3 set. This confirms one report was stored; it does not validate all sensor index behavior, freshness after a write, or automatic allocation.

The teacher fingerprint dialog shows that teacher's preparations and allows cancellation or consent. Cancelling leaves the slot blocked; deleting its teacher or device also removes an unguarded preparation but retains the block. Scanner selection and configuration can change after preparation, so capture must revalidate them. No code may write a sensor slot based on a preparation alone.

Replacement preparation verifies and locks an active teacher's exact finger and slot bound to the selected eligible scanner, then atomically blocks the slot and records the original mapping ID (migration 025). Legacy unbound mappings, mappings bound to a different scanner, and already blocked slots are rejected. Cancellation leaves the slot blocked. The administrator UI is gated by `EnrollmentSecurity:EnableSameSlotReplacement`, off by default. Ordinary enrollment consent, operation lookup, claim and completion reject replacement preparations.

Replacement consent separately records five-minute administrator consent for the exact prepared scanner, teacher, finger, slot and original bound mapping, after rechecking eligibility and the absence of a write guard. Active consent cannot be silently replaced; expired consent is renewed with a new operation ID. Consent alone does not authorize a sensor write; replacement-specific guard and claim transactions revalidate the target and consent.

The replacement guard prerequisite rechecks the exact preparation, bound original mapping, eligible scanner and teacher, active administrator, unexpired replacement consent and operation under transaction locks. It atomically creates the unresolved guard, consumes consent, and invalidates scanner inventory and inventory challenges. The original slot block and mapping remain.

The `replacement-guard-reserve` and challenge endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementGuardReserveApi`. A device-authenticated request binds preparation, operation, slot, nonce and a single-use `replacement_guard_reserve` challenge. On success the signed response appends `guarded_only\n`. This receipt records a server guard only, is not permission to write, and does not prove a scanner journal was persisted. Firmware writes and verifies a prepared replacement journal before contacting the server and retains it on failure.

The replacement-write routine requires a separate, verified, one-use write grant that excludes recovery. It reads back the uncertain journal before issuing a sensor write, and reads back the confirmed journal after a checked acknowledgement.

The `replacement-write-claim` and challenge endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementWriteClaimApi`. A successful authenticated claim requires the original five-minute consent still to be valid and its administrator active; it is recorded once against the guarded operation. Opt-in firmware verifies the exact signed claim before the journalled write routine. A lost claim response leaves the slot blocked; do not delete the claim or guard manually.

The staged `replacement-write-fence` and challenge endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementWriteFenceApi`; **do not enable them**. Firmware source 1.6.33 includes an unconnected routine that durably fences a prepared or uncertain journal before requesting a signed fence receipt. The server fence permanently rejects original claims and completion, and permits a separate administrator recovery intent without removing the claim, guard, or slot block. This relies on the scanner serializing fencing with all sensor writes and discarding any in-memory grant; an authenticated device message is only an attestation, not proof of quiescence. The replacement recovery path is not connected.

Firmware source 1.6.35 also includes an unconnected recovery-attempt reservation routine. It requires the already-fenced journal and a verified recovery intent identifier, durably records a prepared attempt before contacting the server, and retains that journal after a signed `reserved_only` response. The prepared state is distinct from an uncertain recovery write; legacy uncertain attempts remain held. That receipt is not a recovery-write grant. Lost responses require status reconciliation; do not retry an uncertain reservation as a fresh attempt.

The staged `replacement-attempt-claim` endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementAttemptClaimApi`; **do not enable them**. They issue a one-use authenticated grant only for an exact reserved attempt, a matching original-write fence, a still-active administrator and unexpired recovery consent. Firmware source 1.6.37 includes an unconnected exact-grant verifier and journalled recovery write routine: it records and reads back uncertainty before a checked sensor write, and records confirmation only after a checked acknowledgement. An uncertain or lost recovery claim remains blocked.

The staged `replacement-attempt-finish` endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementAttemptFinishApi`; **do not enable them**. A confirmed scanner journal attestation is accepted only for an exact fenced and claimed recovery attempt, and completion retains the original mapping while atomically recording a distinct receipt and removing the guard, attempt and block. The receipt is retryable using a fresh challenge. Firmware source 1.6.38 verifies the exact signed receipt before clearing the same confirmed attempt journal; uncertain attempts remain held. A device MAC attests journal state but does not independently prove sensor contents. The actual recovery write remains disconnected.

The authenticated `replacement-operation` lookup and challenge are behind `EnrollmentSecurity:EnableReplacementOperationApi`, off by default. They return a signed, nonce-bound eligible replacement preparation, operation, and slot without reserving or authorizing a write. The lookup refuses ambiguous results when more than one replacement consent is active.

Firmware 1.6.44 includes a separately selected `Admin → Replace finger` path, gated at build time by both `ENROLLMENT_WRITES_ENABLED=1` and `REPLACEMENT_WRITES_ENABLED=1`. The scanner displays the exact slot and requires `#` confirmation, checks that the slot is occupied, captures a fresh model, journals and reserves the guard, obtains the separate one-use claim, overwrites once, and verifies the completion receipt. The operator must compare the displayed slot to the portal before confirming. Normal `Enroll finger` remains separate. Interruptions and lost responses can leave attendance blocked indefinitely; no automatic recovery is provided.

The server additionally needs `EnrollmentSecurity:EnableSameSlotReplacement`, `EnableReplacementOperationApi`, `EnableReplacementGuardReserveApi`, `EnableReplacementWriteClaimApi`, and `EnableReplacementFinishApi`, all under `EnrollmentSecurity`. They are off by default; the administrator controls and service refuse replacement if any of these flags is off. On the development scanner `1000000005`, one controlled replacement of slot 3 completed on the opt-in build: the server recorded a replacement receipt, retained the existing mapping and binding, and removed the guard and slot block. The checked sensor inventory reported slot 3 occupied afterward. This single test does not establish biometric recognition after replacement or validate interrupted-write behavior. A confirmed journal whose finish response was lost can only be retried if the signed completion endpoint remains available. Do not manually clear an uncertain journal, guard or slot block.

The staged `replacement-attempt-fence` endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementAttemptFenceApi`; **do not enable them yet**. They authenticate a scanner's attestation of an exact durable local recovery-attempt fence, then permanently reject reservation, claims and completion for that attempt. They retain the original guard, attempt, claim, mapping and slot block. A fresh challenge permits retrying a lost receipt, including after consent expires or the current intent changes, while the guard remains. Firmware source 1.6.41 includes an unconnected journal-first routine that verifies the exact signed receipt; confirmed attempts are not fenced. The caller must serialize fencing with all sensor writes and discard any in-memory grant. A device attestation is not proof of quiescence or sensor contents. Neither fence grants a retry; interrupted attempts remain blocked until a safe reauthorization and retry workflow is implemented and verified.

An internal, unexposed retry-consent transaction requires an active administrator, the exact eligible scanner, teacher, finger, slot, original write fence and prior recovery-attempt fence. It retires only that attempt's claim and reservation, creates a new short-lived recovery intent, and retains both fences, the guard, mapping and slot block atomically. This consent is not a scanner write grant. Scanner discovery and journal transition to the new attempt, user interface, and end-to-end power-loss handling are not connected; do not invoke this prerequisite manually.

Firmware source 1.6.42 stages an unconnected journal transition from the exact locally fenced attempt to a distinct prepared retry. It writes and reads back the new attempt identity and never performs a sensor write. Its caller must first verify the server fence receipt and new recovery intent, discard prior grants, and serialize all sensor writes. A failed or interrupted transition stays held for reconciliation; discovery and retry reservation are not yet wired to this routine.

Migration 034 retains issued recovery intent identities while the guard is unresolved. This lets a scanner fence a previously issued attempt after consent expired and was renewed before server reservation, provided no conflicting attempt or claim exists. Its backfill can recover only the intent current at migration time, not older identities already overwritten. A historical identity is not consent or a write grant. Scanner-side reconciliation and the full retry workflow remain unconnected.

The read-only `replacement-guard-status` and challenge endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementGuardStatusApi`. A device-authenticated request binds preparation, operation, slot, nonce and a single-use challenge. The signed response reports `guarded`, `not_guarded` (only while the same replacement consent record exists, even if expired), or `completed` for a matching replacement receipt. Missing, changed or conflicting records fail closed. These states are snapshots, not sensor-write authorization or evidence of sensor contents; even `completed` must never be used to clear an uncertain journal. Firmware source 1.6.29 observes and verifies this status for an uncertain replacement journal in write-enabled builds, logs the result, and keeps the journal held in every case. The endpoint remains off by default.

Migration `028` adds separate replacement completion receipts. The completion transaction rechecks the exact guard, scanner, slot, and original bound mapping, refuses any recovery intent or attempt, and atomically records a replacement-specific receipt before removing the guard, preparation, and slot block. It retains the original mapping and binding. Exact repeat completions are idempotent. Recovery attempts require a separate completion path.

The `replacement-finish` and `replacement-finish/challenge` endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementFinishApi`. The device-authenticated request body is `preparationId\noperationId\nslot\nclientNonce\nconfirmed\n`, and the signed response appends `completed\n`. The purpose-specific challenge is consumed in the same transaction as completion, including an idempotent retry with a fresh challenge. A valid MAC is a scanner's attestation, not proof of the sensor result or durable journal. Opt-in firmware sends this request only for a valid confirmed replacement journal and clears that same journal only after verifying the exact signed receipt; failure remains held.

Firmware source 1.6.23 introduced a versioned replacement journal format. Existing enrollment journals retain their original format; older firmware rejects the new format rather than interpreting it as an ordinary enrollment. Current opt-in source can create a replacement journal; an uncertain journal remains held and no automatic recovery is provided.

Migration 026 and an internal recovery-intent store are prerequisites for an administrator-authorized fresh enrollment into an uncertain replacement's same slot. They record a five-minute intent tied to the exact guarded operation, administrator, teacher, finger, slot, and eligible scanner, after rechecking the original bound mapping. Only one active intent is allowed; an expired intent can be replaced with a fresh operation ID. Recording an intent is not exposed in the portal, and it neither grants permission to write nor clears the journal, guard, or slot block. A future authenticated recovery protocol must revalidate consent and coordinate both durable states before any retry.

The experimental `replacement-intent` and `replacement-intent/challenge` endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementIntentApi`. They follow the same device API-key and HTTPS policy as the other enrollment routes. A request authenticated with the scanner key and a single-use `replacement_intent` challenge carries the preparation, guarded operation, slot, and client nonce. A signed `intent_only` response includes those values and the recovery operation ID after eligibility and intent expiry are rechecked. This is read-only observation, not permission to write; firmware does not yet use it, and consent must be revalidated during any future claim.

Firmware source 1.6.24 adds an unconnected response verifier that compares the returned preparation, guarded operation, and slot with a replacement journal, validates the nonce and recovery operation format, and requires a caller-supplied MAC verifier. It does not fetch intents or change the journal, sensor, or server guard.

Firmware source 1.6.25 connects this verifier to a read-only intent lookup when the admin starts enrollment and a replacement journal is held. A verified intent is informational only: the scanner still reports recovery held and does not capture, write, clear the journal, or change the server guard. An unavailable or invalid response also remains held. The endpoint remains disabled by default.

Migration 027 and an internal attempt reservation record the exact guard and recovery operation after rechecking the active intent and target. An unresolved attempt prevents another intent or attempt, even after consent expires, and its foreign key prevents guard deletion. This reservation is not a scanner write claim or authorization and has no API or cleanup path. A future authenticated protocol must coordinate the scanner's durable journal, validate a fresh claim, and resolve or recover the attempt before replacement writes can be enabled.

Firmware source 1.6.26 adds a separate, checksummed recovery-attempt journal format containing both the original guarded operation and the recovery operation. Existing journal formats remain readable; older firmware rejects the new format. The internal transition only records an attempt for the exact uncertain replacement journal and does not permit a sensor write, clear a journal, or communicate with the server. An attempted transition with an uncertain storage result must remain held; a future protocol must reconcile it with the server before any retry. The transition is not connected to the scanner flow.

The read-only `replacement-attempt-status` and `replacement-attempt-status/challenge` endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementAttemptStatusApi`. Authenticated requests and signed responses bind the device, preparation, guarded operation, recovery operation, slot, fresh challenge, and client nonce. A matching existing attempt reports `reserved`; if no attempt exists and the recorded intent still identifies the requested operations, it reports `not_reserved` even if that intent has expired. Conflicting or missing records fail closed. These are point-in-time observations, not write permission or proof of sensor contents; they do not prevent a later reservation, clear a guard, or enable retry. Firmware source 1.6.27 checks and verifies this status for a held recovery-attempt journal in write-enabled builds, but leaves the journal and slot blocked regardless of the result.

The separate `replacement-attempt-reserve` and `replacement-attempt-reserve/challenge` endpoints are disabled by default behind `EnrollmentSecurity:EnableReplacementAttemptReserveApi`. An authenticated request consumes its purpose-specific challenge and atomically reserves the exact recovery attempt only while the administrator's intent and all scanner, teacher, mapping, guard, and slot checks remain valid. The signed `reserved_only` receipt records server state, **not permission to write** and not proof that the scanner journal was persisted. A lost response can be reconciled with the status endpoint. The original guard and slot block remain; firmware does not yet request reservations and replacement writes remain disabled.

Administrators can select a preparation and record five-minute consent for its displayed teacher, finger, scanner, and slot. The server rejects occupied-slot consent and claims reporting an occupied slot, including claims using previously stored occupied-slot consent; replacement is not supported yet. The server cannot independently verify the scanner's reported occupancy. Consent can be revoked while no write is unresolved, expires automatically, and is removed when its preparation is cancelled. Creating consent rechecks the administrator, preparation, selected scanner, room, teacher, slot block, and existing mappings; it does not inspect the sensor or start enrollment. Migration 023 is required for the preparation list and consent controls. When enabled, `POST /api/device/enrollment/operation` returns only an eligible preparation with active admin consent for that paired, selected scanner. It is read-only and does not consume consent or authorize a write. Migration 023 is required for this route's authorization lookup.

An internal write guard can durably mark a preparation as unresolved using a unique operation ID; the same operation can be retried idempotently. A guarded preparation cannot be cancelled, unblocked, or removed through its teacher or scanner; changing the selected scanner, server-initiated fingerprint clearing, and system reset are also rejected while any guard exists. The affected teacher and classroom cannot be deactivated, and the affected scanner cannot be moved, deactivated, or have enrollment disabled. This does not prevent physical or older-firmware sensor wipes. The portal displays an unresolved guard. An authenticated finish route can atomically create a scanner-bound mapping, record a completion receipt, and remove the guard and block; a retry with a fresh challenge returns the same signed completion for the same operation. Firmware retains its confirmed journal until it verifies the signed completion, or verifies a completed recovery response and sensor occupancy after a lost response. One hardware enrollment succeeded; the protocol and recovery remain experimental. Do not remove guards or blocks manually.

The legacy enrollment API is disabled until a safe enrollment workflow is implemented; for a recognized device it returns HTTP 503. Existing fingerprint scan mappings remain global, not device-specific, so do not use multiple scanners for legacy attendance. The new completion path binds its future mappings to the original scanner, and the binding survives scanner deletion; it does not synchronize templates or make it safe to replace a scanner. Migration 020 is required for fingerprint lookups; if absent, they fail instead of bypassing bindings. Explicit replacement is not yet available. The new template encryption component is not connected to enrollment or storage, and does not provide encrypted device transport or synchronization.

The server and firmware also contain a matching enrollment message-authentication primitive, used by the experimental recovery, claim, and finish endpoints, not as encryption. It authenticates a message's direction, device ID, operation, challenge, and SHA-256 body digest with HMAC-SHA-256. It does **not** encrypt data or prevent replay by itself: the protocol uses per-scanner keys, single-use challenges, authenticated responses, and durable recovery state. Do not send PINs, secrets, or biometric data using this primitive as a substitute for encrypted transport. Firmware 1.6.20 contains the client and checked sensor adapters; enrollment writes are enabled by default starting with source 1.6.61.

An internal challenge store generates 32-byte random challenges, stores only their hashes, expires them after two minutes, and atomically consumes a matching challenge after MAC verification. It limits each active paired device to 16 outstanding challenges, and survives process restarts. Recovery challenges do not issue write permissions. Claim challenges are consumed transactionally with admin-consent validation and write-guard creation. The outstanding-challenge cap is not production-grade rate limiting and can be exhausted by anyone with the shared fleet key. All enrollment APIs below remain disabled by default.

The authenticated completion route binds a `finish` challenge and MAC to the preparation, operation, slot, and confirmed state, loads the scanner's stored key, and consumes the challenge in the same transaction as the mapping and receipt. A failed transaction leaves the challenge available until expiry; successful retries require a fresh challenge and return a signed completed receipt. Migration 022 records the slot in new completion receipts; older receipts without a slot cannot be authenticated through this route. It relies on the firmware's confirmed journal and checked sensor acknowledgement; one enrollment succeeded on hardware, but interrupted-write recovery has not been tested on hardware.

Experimental enrollment operation, claim, finish, and recovery endpoints are available and disabled by default: `POST /api/device/enrollment/operation` (`EnrollmentSecurity:EnableOperationApi`), `POST /api/device/enrollment/claim/challenge` and `POST /api/device/enrollment/claim` (`EnrollmentSecurity:EnableClaimApi`), `POST /api/device/enrollment/finish/challenge` and `POST /api/device/enrollment/finish` (`EnrollmentSecurity:EnableFinishApi`), and the recovery challenge/response endpoints (`EnrollmentSecurity:EnableRecoveryApi`). They require the shared fleet API key. To activate them for local development, set all four flags to `true` in Development User Secrets, configure a persistent random 32-byte Base64 `EnrollmentSecurity:MasterKey`, and restart the app. HTTP is allowed only in the Development environment; other environments reject HTTP with status 426 before device authentication or protocol processing. HTTP exposes device keys to anyone able to observe the development network, so use only on a trusted LAN. The operation endpoint returns only the selected scanner's currently eligible, admin-consented preparation; it does not consume consent. A claim additionally requires the scanner's unique key and a fresh, single-use `claim` challenge, and atomically consumes consent while creating the write guard. The signed claim response is the firmware's authorization gate, not evidence that a sensor write succeeded. The finish request is accepted only with the scanner key and a fresh challenge; the server atomically commits the mapping and receipt, then signs `preparationId\noperationId\nslot\ncompleted\n`. Recovery reports an exact scanner, preparation, operation, and slot as `unresolved` or `completed`; a verified completed state plus an occupied slot allows firmware to clear its retained journal after a lost finish response. Firmware checks and handles an existing journal before requesting a new operation, avoids recovery calls when no journal exists, verifies recovery, claim, and finish responses, and runs the checked flow only when compiled with `ENROLLMENT_WRITES_ENABLED=1`. Source 1.6.61 enables this flag by default. One enrollment and attendance scan succeeded on the opt-in 1.6.20 build on COM9.

The operation request contains `deviceId` and returns `preparationId`, `operationId`, and `slot` only while the matching admin consent is active. Each challenge request contains `deviceId`. The recovery request contains `deviceId`, `preparationId`, `operationId`, `slot`, `challenge`, `clientNonce`, and `tag`; challenge, client nonce, and tag are lowercase 64-character hexadecimal strings. The claim request contains `deviceId`, `preparationId`, `operationId`, `slot`, `occupied`, `challenge`, and `tag`. Firmware currently submits `occupied: false`; it never authorizes an occupied-slot replacement. Claim responses are signed over `preparationId\noperationId\nslot\nempty\ngranted\n`. The finish request contains `deviceId`, `preparationId`, `operationId`, `slot`, `challenge`, and `tag`; its signed request body is `preparationId\noperationId\nslot\nconfirmed\n`, and the server response adds `completed\n`. The scanner must generate a new unpredictable 32-byte client nonce for every recovery attempt and check that the signed response contains that exact nonce, even if an old server challenge is replayed. The authenticated recovery request body is ASCII `preparationId\noperationId\nslot\nclientNonce\n`, and its signed response body adds `unresolved\n` or `completed\n`. GUIDs are canonical lowercase, the slot is decimal, and every field ends with a newline. Errors are unsigned and must never be treated as success. A lost response requires a fresh challenge and nonce; the device must not blindly retry a claim because it creates a durable guard. These APIs do not encrypt traffic; the existing shared fleet key is exposed on plain HTTP, and the per-device key provisioning path is not production-ready.

The internal enrollment credential store encrypts each 32-byte device key using AES-GCM with a separate 32-byte server master key and device-bound associated data. It rejects duplicate device keys, incorrect master keys, replacement, and initial provisioning while that scanner has an unresolved write. The recovery endpoint uses the stored key for authentication and response signing; this does not authorize enrollment.

An experimental USB-only provisioning path consists of `scripts\provision-enrollment-key.ps1`, the server's `--enrollment-local-key` mode, and the scanner's `SC-KEY-*` serial protocol. It verifies the reported paired device ID, uses a random per-device key stored encrypted on the server, sends that key over the selected USB serial port, and verifies a challenge response. It refuses to overwrite a different existing scanner key; retrying after an interrupted transfer uses the existing server key. This path was used on COM9 for device `1000000005` and its challenge response verified. The USB connection and selected scanner must be trusted: a malicious USB device can impersonate the requested ID and receive the key. As explicitly chosen for this installation, the key is stored in ordinary ESP32 flash; physical access may expose it. USB provisioning does not protect HTTP traffic, and successful key provisioning does not validate fingerprint capture or sensor writes.

The local development setup now has a Base64 random 32-byte master key in User Secrets and a provisioned key for device `1000000005`; no key values are stored in the repository. There is no key rotation/recovery procedure. Back up the master key separately: losing it prevents recovery of stored scanner keys, and changing it is rejected. After reset or scanner replacement, do not erase or re-pair the device until a safe key recovery process exists. This storage does not encrypt network traffic or protect keys from a compromised server.

For the interim workflow, use one physical scanner for both teacher enrollment and attendance in its assigned classroom. Development route flags must be enabled in local User Secrets, and the application must be started in Development for HTTP enrollment routes to work. Verify the firmware actually installed on the scanner: source changes do not update a device until built and uploaded. Device `1000000005` has a verified protocol key and completed one manual enrollment and attendance scan; automatic slot allocation and the keypad enrollment entry have not yet been validated end-to-end on hardware. Do not treat fingerprints enrolled locally by diagnostic sketches as teacher enrollments: they have no corresponding server mapping and an existing legacy mapping for the same slot could identify a different teacher. The device previously reported sensor slots 2 and 3 occupied; do not replace or clear them. Before enrollment, confirm the intended teacher, finger, scanner, and automatically reserved slot.

After migration `016` has been applied and the updated app is running, an admin can block a slot under **System settings → Block a sensor slot**. A block prevents attendance matching for that slot on every scanner, without changing its server mapping or sensor contents. Blocks persist through fingerprint clears and resets; the portal does not yet support unblocking. Do not unblock by editing the database unless the physical slot and its mapping have been verified and a safe replacement procedure is available. If migration `016` is missing, fingerprint matching fails rather than bypassing the block.

Legacy repository enrollment also rejects blocked slots. Older scanner firmware writes a sensor slot before contacting the now-disabled API and may attempt to delete it when that call fails. Do not use legacy enrollment on older firmware; disabling the API alone does not protect its sensor contents.

The **Inspect slot** action shows the current global server mapping and block status, including inactive teachers. It does not communicate with the sensor and cannot establish which fingerprint is physically stored there.

Admins can permanently delete a device after ending any active session and turning off its assigned room's AC. Verify the physical relay is off before disconnecting: deleting the device prevents further status sync and does not erase fingerprints stored on its sensor. Attendance history and classroom settings are retained.

**Remote AC from Admin:** **Admin → AC control** uses per-room **On** / **Off** in the status table (`RemoteOn` → `Remote`, `RemoteOff` → `Off`). That updates MariaDB desired state; firmware `1.2.12+` polls `/api/device/status` about every 30s (with `/health`) and applies `acOn` without a fingerprint scan. Firmware `1.2.13+` keeps the idle LCD on **Ready to scan** if a status sync blip fails (health still drives online/offline). Remote / Override stay on until an explicit Off (portal, hard shutdown, scan end, or clear-session); schedule-window auto-off only clears session-tied `Cooling`.

**Clear fingerprints from Admin:** **Admin → System settings → Clear fingerprint device data** deletes all `fingerprint_templates` rows and sets `pending_clear_fingerprints` on every active scanner. Firmware `1.2.15+` reads `clearFingerprints: true` from `/status`, runs `emptyDatabase()` on the sensor, then `POST /api/device/clear-session` (AC off + clears the pending flag). Teachers must re-enroll afterward.

**Server health:** firmware `1.2.9+` polls `GET /health` every 30s (and on boot / WiFi reconnect / Serial `HEALTH`). Idle LCD (firmware `1.2.14+`) keeps **Ready to scan** on row 1 and shows **Offline** on row 2 when WiFi is up but SmartClass is unreachable (or WiFi is down); fingerprint scans are blocked until health returns Healthy (HTTP 200).

**Attendance vs AC re-entry:** the first in-window scan creates one attendance session (`started` → AC Cooling); the next ends it and records time-out. The room remains on if a different class is active or currently scheduled, otherwise it turns off, even if the ending class's own window remains open. A further scan in the same window reopens that attendance row regardless of the room's existing AC status; the next scan ends it again. Outside the window, scans return `no_schedule`. Automatic missing-time-out closure applies the same overlap rule. Reconciliation clears attendance power when no active session or in-window schedule remains; `Remote` / `Override` status is not automatically cleared.

### Device PIN

- Auto-created when an admin creates a teacher (random 6-digit unless supplied).
- Visible and **editable** on **Teacher → My profile**.
- Previously used for legacy enrollment; that flow is now disabled.
- Demo: Maria **1001**, John **1002**, Ana **1003**.

### Enrollment

The legacy enrollment endpoint is disabled. Do not send PINs to it or use older firmware's enrollment command. The experimental consent-based workflow is described above; replacing an occupied sensor slot is not supported.

### Scan (curl)

```powershell
curl -X POST http://localhost:5062/api/device/fingerprint/scan `
  -H "Content-Type: application/json" `
  -H "X-Device-Api-Key: your-long-random-key" `
  -d '{ "templateId": 12, "deviceId": "1000000001" }'
```

| `action` | Meaning |
|----------|---------|
| `started` | In-window schedule for **this classroom** started |
| `ended` | Active class in **this classroom** closed |
| `unknown` | Template ID not enrolled |
| `unknown_device` | `deviceId` not registered / inactive |
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
| `POST /api/device/fingerprint/enroll` | `{ "pinCode", "templateId", "deviceId" }` | Disabled until safe enrollment is implemented |
| `POST /api/device/fingerprint/scan` | `{ "templateId" }` | Unchanged contract: attendance + classroom AC status |

### Firmware (implemented)

1. **Config** — `baseUrl` (e.g. `http://192.168.x.x:5062`), `deviceApiKey`, remove old `:3001/attendance`.
2. **HTTP** — JSON POST, `X-Device-Api-Key`, parse `ok` / `action` from the body.
3. **Enroll** — firmware checks for pending recovery first, then can fetch an active admin-consented operation and run the checked capture/claim/journal/finish flow only when compiled with `ENROLLMENT_WRITES_ENABLED=1`; source 1.6.61 enables it by default. After reserving an automatically selected slot and recording consent in the portal, press `#`, select **Admin**, enter the device admin PIN, then select **Enroll finger** on the keypad within five minutes. Follow the LCD prompts. A pending or uncertain result requires investigation; do not remove guards or blocks manually. Serial `ENROLL` remains available for diagnostics. To disable enrollment and inventory in a build, pass `--build-property "compiler.cpp.extra_flags=-DENROLLMENT_WRITES_ENABLED=0"` to `arduino-cli compile`.
4. **Scan** — On match, `POST` scan; `started`/`ac_on` → AC ON; `ended`/`ac_off` → AC OFF; deny paths do **not** flip AC.
5. **AC** — LCD icons; optional `#define AC_RELAY_PIN -1` for a physical relay later. Desired state also comes from Admin remote on/off via `/api/device/status` poll.
6. **Temperature** — DHT for LCD idle display only (optional later: send room temp on scan).

### Out of scope (still)

- Hardware validation and operator recovery for interrupted sensor writes.
- Push/MQTT command channel (devices pull desired AC state via `/status`).

### Status

| Item | Status |
|------|--------|
| Legacy enroll API (`pinCode` + `templateId`) | Disabled |
| Scan API + attendance / AC | Done |
| Admin remote AC on/off + device status poll | Done |
| Bruno + README docs | Done |
| ESP32 JSON + API key | Done |
| Serial `ENROLL` | Disabled by default in firmware `1.6.20`; no hardware write performed |
| Teacher portal PIN view/edit | Done |
| Keypad on device | Future |

---

## ESP32 firmware

Companion project: `SmartClassAC-ESP32/smartclass/` (`smartclass.ino`).

**Versioning:** bump `FIRMWARE_VERSION` in the sketch on every firmware change (semver). Keep the in-file changelog comment updated. Version is shown on the LCD splash, Serial boot banner, and `STATUS`.

| Constant | Purpose |
|----------|---------|
| `FIRMWARE_VERSION` | Sketch semver (source currently `1.6.61`) |
| `ssid` / `password` | Wi-Fi |
| `baseUrl` | PC running SmartClass, e.g. `http://192.168.x.x:5062` |
| `deviceApiKey` | Same as `DeviceApi:ApiKey` |
| `deviceId` | Generated device ID (e.g. `1000000001`) — must match `biometric_devices.device_id` |
| `AC_RELAY_PIN` | GPIO for AC relay, or `-1` disabled |
| GSM UART | SIM900L on UART1: ESP32 RX GPIO18 ← modem TX; ESP32 TX GPIO19 → modem RX; 9600 baud |

Serial Monitor **115200**:

| Command | Action |
|---------|--------|
| `HELP` | List commands + firmware version |
| `STATUS` | Firmware version, Wi-Fi, GSM modem/SIM/network state, `deviceId`, sensor template count |
| `GSM` | Repeat the SIM900L modem, SIM, network-registration, and signal checks |
| `ENROLL` | Default build reports enrollment disabled; the gated flow runs only in an explicitly enabled test/deployment build |

The current sketch does not automatically run the SIM900L startup check; the call is commented out. Use the `GSM` serial command to run diagnostics at **115200** and check modem, SIM, network registration and signal quality (`99` means unknown/unavailable). The firmware also queries `AT+CNUM` and may log a phone number if the SIM/network provides it; do not share that output. Firmware 1.6.47 no longer sends the boot status SMS; earlier versions may still send it. The portal's SMS transport uses a separate server-connected modem.

Power the SIM900L from a supply appropriate for the exact module/breakout; cellular transmit bursts can require high current, so do not assume the ESP32 3.3 V pin can power it. Share ground with the ESP32, cross TX/RX as shown above, and verify the board's UART logic-level compatibility before connecting GPIO19 to the modem RX.

The portal's separate **GSM settings** view can list Windows COM ports and save a selected port and standard baud rate (migration `035`). Discovery does not distinguish the USB modem from other serial devices; verify the port before selecting it. A serialized SIM800L SMS transport and the Faculty OTP API have been tested with a fake serial connection, not a real modem. Modem acceptance of an SMS is not proof of handset delivery; an interrupted submission has an uncertain outcome and must not be blindly retried.

Administrators can use **System → GSM settings → Test SMS** to send the fixed message “SmartClassAC modem test message.” Enter an `09xxxxxxxxx` recipient, save and verify the modem port and baud rate, then explicitly confirm the recipient and port before sending. Standard SMS charges may apply. This test uses the saved modem settings independently of Faculty OTP configuration and does not issue an OTP. If delivery is uncertain, check the phone before attempting another send.

The Faculty OTP store (migration `036`) reserves six-digit codes for five minutes, activates them only after modem acceptance, limits requests to three per phone and ten per scanner in a five-minute fixed window, and allows at most five guesses and one successful use. Codes and phone numbers are represented by keyed digests in the OTP tables; verification rechecks the teacher's current status and phone. The device API is **disabled by default** (`FacultyOtp:EnableDeviceApi`). Firmware source `1.6.53` replaces the demo Faculty ID/PIN with an 11-digit mobile number and six-digit SMS code, verifies the signed response before allowing access, and fails closed if the API is unavailable. It has been compiled but not uploaded or hardware-tested; previously installed firmware may still have the insecure demo login.

Portal teacher registration and mobile changes use a separate SMS verification
workflow (migration `043`), not the scanner OTP endpoints. Configure the GSM
modem under **System → GSM settings** and retain a stable random 32-byte
Base64 `FacultyOtp:Secret` in protected configuration. The admin receives a
six-digit code at the proposed number when creating a teacher or changing that
number; a teacher changing their own number follows the same flow. Other profile
edits do not need an OTP. Codes expire in five minutes, allow five guesses, and
are bound to the signed-in actor, operation, teacher (for updates), and proposed
number. Limits are three requests per number and ten per actor in five minutes,
separate from scanner limits. Successful verification and the account change
commit together. A failed, uncertain, expired or superseded SMS submission
cannot authorize the change. Modem acceptance does not guarantee delivery;
check the handset before retrying an uncertain submission. Existing teacher
numbers are not retroactively verified. Seeded demo accounts bypass the portal
registration workflow and must not be used as evidence of verified ownership.
The GSM workflow has been tested with a simulated modem, not live SMS.

Faculty OTP failure logs report the modem result and whether the reservation was updated, without phone numbers or codes. `DeliveryUnknown` means the message may have been submitted, but its OTP is not activated; check the handset before another request. `Unavailable` indicates a port or communication failure, `Rejected` a modem rejection before submission, and `Accepted` with an unchanged reservation can mean the request was superseded or expired, or the device became ineligible before activation. The device's generic request response does not confirm SMS delivery.

The API uses the fleet API key plus each paired scanner's provisioned enrollment key and a purpose-specific, single-use challenge. Outside Development it requires HTTPS; Development HTTP exposes phone numbers and OTPs to anyone who can observe the trusted LAN. When the complete flow is ready, configure `FacultyOtp:Secret` as a separate, stable, randomly generated Base64-encoded 32-byte secret in protected configuration, shared across portal instances and retained across restarts. Rotating it invalidates outstanding OTPs and resets the effectiveness of phone-keyed limits until existing windows expire. Never put this secret in source control.

The request and verify routes are `POST /api/device/faculty/otp/request` and `POST /api/device/faculty/otp/verify`, each with a corresponding `/challenge` route. An operation's signed body is the ASCII phone number or six-digit code followed by a newline; its purpose is respectively `faculty_otp_request` or `faculty_otp_verify`. The signed response body contains the lowercase SHA-256 digest of that request body, a newline, the status, and a final newline. The request status is always `processed` for authenticated valid-format requests, regardless of account or delivery outcome; it does not promise that an SMS was sent. Verification returns `accepted` or `denied`. Clients must validate the response MAC, challenge, request digest, and exact status before granting access. Response timing can still differ by delivery outcome, so the generic response is not a timing-side-channel guarantee. Never log the request or response body, phone number, code, or modem commands.

Administrators can set a classroom's inclusive teacher minimum (16–30°C; migration `037` defaults existing rooms to 16°C). Teacher portal and scanner target changes require an active class in that room and cannot set a target below the minimum; administrators retain their existing control. A saved target is not proof of an AC change.

New rooms no longer initialize `current_temperature` from the target, and target commands no longer overwrite it. Earlier stored values may have been copied from targets and are not reliable measurements; the dashboard overview displays the explicitly labeled target instead.

Experimental IR synchronization is disabled by default in portal configuration (`AcIr:EnableDeviceApi`); the current sketch sets `AC_IR_ENABLED=1`. The source uses GPIO23 with IRremoteESP8266 and NEC 32-bit up `0x08E7A857` and down `0x08E7906F`. These relative commands cannot verify AC reception or its physical setpoint: missed commands or another remote can cause drift, so the minimum is not guaranteed physically. Verify the IR module is a suitable transmitter, its wiring, 3.3V input compatibility and power requirements before use.

The companion firmware repository includes a separate `ir_receiver_diagnostic` sketch for an IdeaSpark ESP32 with an integrated ST7789 display and a 38 kHz IR receiver on GPIO13. It labels the temperature commands and power toggle; see its README for wiring and use. It cannot confirm reception or state at the actual AC.

Firmware source `1.6.57` runs periodic health, room-status, and buzzer network requests in a background task to keep the keypad responsive during number and code entry. User-initiated requests, such as submitting an OTP or changing a target, can still take time and show a waiting message. If the background task cannot start, the scanner logs an error and falls back to blocking polling. Network delays, other blocking scanner operations, and offline periods can still delay or miss buzzer reminders.

Migration `039` stores the boot, assumed setpoint, pending step and audit history. After every scanner restart, an administrator must use the remote to set the assigned AC to the current portal target and confirm it under **AC control → Reconcile**. The confirmation is tied to that boot, room and unchanged target. A relative step is recorded as in flight before it can be transmitted; an unknown claim or acknowledgement halts the scanner until restart and reconciliation, rather than retrying. Reassignment, deactivation, re-pairing, deletion, room retirement and reset are blocked while a step is in flight; otherwise changing assignment or pairing, deactivating the scanner, or retiring its room invalidates its IR state and requires a fresh boot. The AC control page distinguishes reconciliation, pending, uncertain and not-recently-seen states; its two-minute heartbeat is not proof the scanner is online. Apply migration `039` before deploying this portal version even if IR is disabled, because device and classroom management checks the IR state.

Firmware source `1.6.58` checks for pending IR steps every five seconds while on the normal scanning screen, instead of every ten seconds. Exit the Faculty menu after saving a target to allow IR polling. Network authentication and other scanner work add delay; each degree requires a separately claimed and acknowledged step. More frequent polling increases server requests and still does not verify physical AC reception.

**Experimental attendance power toggle:** migration `042` adds an attendance-derived desired power state, an assumed power state and guarded in-flight toggle. Apply migrations before deploying this portal. Scanner source `1.6.60` enables `AC_IR_POWER_ENABLED=1` by default; all scanners must use power-capable firmware compiled with that flag enabled, and the portal must have both `AcIr:EnableDeviceApi=true` and `AcIr:EnablePowerToggle=true`. The portal flags remain off by default. A v2 scanner cannot synchronize IR with a portal that has power toggle disabled; the portal power flag globally disables legacy IR routes, so old scanners cannot synchronize IR until upgraded or the flag is turned off. Switching protocol versions requires a fresh scanner restart. Firmware source `1.6.60` has not been uploaded or physically tested as part of this change.

After each restart, an admin must set the AC to the target temperature with its remote, physically verify whether it is ON or OFF, and select that state in **AC control → Reconcile**. Reconciliation can immediately result in a toggle if the confirmed state differs from attendance demand. The NEC `0x08E700FF` command is one toggle, not separate ON/OFF codes; a missed signal or manual remote use can invalidate the assumption. After any uncertain claim or completion, restart and physically reconcile; never retry the toggle blindly. Temperature steps wait while assumed power is off. Admin/teacher Remote or Override controls retain their existing room/relay behavior and are not IR power-toggle requests. Neither server acknowledgment nor the diagnostic receiver proves that the AC received or applied a signal.

Migration `038` adds a five-minute scanner-bound teacher session. Session creation consumes a delivered OTP atomically and stores only a hash of a random token; session lookup rechecks the paired scanner, pairing timestamp, active teacher and phone. An authenticated query returns only that teacher's schedules for the current school date, across classrooms. These menu routes are separately gated by `FacultyOtp:EnableMenuApi` (default false) in addition to `FacultyOtp:EnableDeviceApi`. Scanner target changes additionally require `FacultyOtp:EnableAcTargetApi` (default false). Firmware source `1.6.53` has been compiled but not uploaded or hardware-tested; validate the complete deployment before enabling menu flags.

On the scanner schedule screen, `*` and `#` navigate today's classes (up to the first 1,000), `0` toggles subject/room, and holding `0` returns to the Faculty menu. The second row shows class times. AC info displays the assigned room's portal target and teacher minimum, not a measured AC setpoint. On AC info `*` lowers and `#` raises the target by one degree, `0` refreshes and holding `0` returns. An uncertain target request must be followed by reopening AC info, not a blind retry. Attendance remains fingerprint-based.

The new `/api/device/faculty/menu/login` and `/schedule` routes each have a `/challenge` route and use purposes `faculty_menu_login` and `faculty_menu_schedule`. The request MAC covers ASCII `value\nindex\n`, where value is the six-digit OTP for login or the 64-character session token for schedule; login requires index zero. Signed responses begin with the lowercase SHA-256 digest of the request body and a newline. Login returns `denied\n` or `accepted\n<token>\n`; schedule returns `denied\n`, `end\n`, or `item\n<index>\n<count>\n<subject>\n<room>\n<start HH:mm>\n<end HH:mm>\n`. Subject and room are limited to 16 printable ASCII characters, replacing unsupported characters with `?`. Session tokens are credentials: do not log them or response bodies; HTTP on a Development LAN exposes them to observers.

The read-only `/api/device/faculty/menu/ac` and `/challenge` routes use purpose `faculty_menu_ac`, the session token and index zero with the same signed protocol. The response is `denied\n` for an invalid session or unavailable assignment, or `info\n<target>\n<minimum>\n<active-class 0 or 1>\n`. The active-class value is informational and is not an authorization for a future temperature change.

Firmware uses `/api/device/faculty/menu/ac-v2`, purpose `faculty_menu_ac_v2`, which adds the assigned room ID before the target in that response. `/api/device/faculty/menu/ac-target` uses `faculty_menu_ac_target` and signs `token\n<old target * 100 + new target>\n<room id>\n`; the server rechecks the assigned room, session, attendance, minimum and old target transactionally. Signed results are `saved`, `denied`, `no_class`, `minimum`, or `stale`. The IR `/api/device/ac-ir/boot`, `/claim`, and `/complete` routes each have a challenge endpoint and use a signed boot identifier plus command identifier. A claimed step is never proof of delivery.

Before enabling Faculty OTP and menu routes, apply migrations `035` through `039`, configure the protected server secret and the scanner's existing enrollment key, select the verified USB modem COM port, and deploy a user-built `1.6.53` or newer sketch after validation. The current sketch sets `FACULTY_OTP_ALLOW_HTTP=1` for Development: HTTP exposes phone numbers, OTPs, and session tokens and must be used only on an explicitly trusted LAN. Set it to `0` for HTTPS and set `facultyOtpCaCertificate` in the sketch to the PEM CA that validates the portal hostname; an empty CA fails closed. Configure the portal's matching HTTPS endpoint and a valid certificate; the separate legacy scanner requests have not been validated end-to-end over HTTPS. The request screen may block while the modem responds; a timeout or unknown delivery outcome does not grant access. Obtain consent before sending a live test SMS.

Usual use: device boots in **scanning mode** (`Ready to scan`). After a successful status sync, row 1 alternates every four seconds between the assigned classroom name (or **Unassigned**) and the ready/disabled prompt or active teacher name. Room names are truncated to the 16-column LCD; the last synced room is shown only while the server is online. Place an enrolled finger anytime → local match → `POST /api/device/fingerprint/scan` for attendance logging → AC follows `started` / `ended` / `ac_on` / `ac_off`. While AC is on, row 2 shows **clock · session timer · temp · AC icon** (both times use a blinking colon). Overlays return to scanning automatically.

Serial Monitor tip (firmware `1.2.6+`): set line ending to **Newline** or **Both NL & CR**. Commands: `ENROLL`, `DELETEALL` (wipe sensor templates + turn AC off / clear room session), `STATUS`, `HEALTH`, `GSM`, `HELP`. Older builds used blocking `readStringUntil`, which stalled the loop (~1s) whenever USB noise arrived without a newline and made the device look “busy”.

If Serial Monitor shows only □□□ / `` boxes: baud must be **115200** (match `SERIAL_BAUD`). Also check fingerprint wiring — sensor **TX → GPIO16**, **RX → GPIO17**; never onto GPIO1/3 (USB). Tools → board option **USB CDC On Boot = Disabled** for classic ESP32-WROOM USB-UART boards. Flash `1.2.8+`, press reset, and you should see an ASCII `SmartClass device booting` banner first.

Templates live on the **physical sensor**. Demo database fingerprint IDs alone cannot match without corresponding sensor templates. Do not use the legacy serial enrollment flow; the replacement workflow is pending.

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
| Enroll fingerprint | Unavailable pending safe enrollment |
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

| Employee # | PIN | Mobile | Username | Fingerprints | Schedules |
|------------|-----|--------|----------|--------------|-----------|
| EMP-1001 | 1001 | `09111111111` | `msantos` | **1**, **2** | Today in-window Math (Room 101); tomorrow Math; today afternoon Science Lab |
| EMP-1002 | 1002 | `09222222222` | `jreyes` | none | Today in-window English (Room 202); yesterday English |
| EMP-1003 | 1003 | `09333333333` | `acruz` | none | **None** |

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
9. Admin add teacher — mobile `09xxxxxxxxx` required; email optional; registration requires SMS OTP verification.

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

**LAN / ESP32 access:** The development device API uses `http://0.0.0.0:5062`; HTTP is permitted for enrollment while the app runs in Development, but device keys are exposed to anyone able to observe that network traffic. Keep this limited to a trusted development LAN. For production traffic, configure Kestrel for direct HTTPS with a server certificate from a private CA; store the PFX path and password in User Secrets or environment variables (for example `Kestrel:Certificates:Default:Path` and `Kestrel:Certificates:Default:Password`), bind `https://0.0.0.0:7044`, and use a stable DNS name or IP included in the certificate's subject alternative names. Non-development server environments continue to require HTTPS for enrollment.

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
| `POST /api/device/fingerprint/enroll` | Disabled pending safe enrollment |
| `POST /api/device/fingerprint/scan` | Identify → start/end class |

On the login page, **Remember username on this device** saves the entered
username in the browser's local storage when the form is submitted, including
unsuccessful sign-in attempts. Uncheck it to remove the saved username. Use it
only on a trusted device. Passwords are not stored by this option; saving and
autofilling passwords is controlled by the browser's password manager. This
option does not keep the user signed in.

---

## Admin and teacher workflow

**Admin:** create/edit teachers (name, employee number, username, **required mobile** `09xxxxxxxxx`, optional email, optional device PIN and password; **active/inactive** status — only active teachers appear when creating schedules or enrolling in a semester; new registrations and mobile-number changes require SMS OTP verification), **semesters** (enroll teachers), classrooms, schedules with **Regular/Makeup** kind and optional **repeat through semester end** (None / Weekly / Mon–Wed / Tue–Thu / MWF / TTH / Custom weekdays — expands to one `class_schedules` row per date; conflicts are skipped and reported); **move** a single schedule to another date/time; system reset (`RESET`) / delete.

From **Admin → Classrooms**, select **View schedules** in the room's Schedules column to view its schedules, then move or delete individual entries. Current and future schedules must be moved or deleted before a classroom can be retired; device assignments are managed separately in Devices.

**Devices:** Admin → Management → **Devices** registers scanners and manages their room assignment, display name, room target temperature, status polling interval, active state, and attendance scan/fingerprint enrollment permissions. Devices fetch these settings after connecting and during status polls; Wi-Fi/server/API credentials stay provisioned locally.

**Teacher:** schedule (shows semester + kind), profile (PIN), attendance, assigned rooms, and active class start/end. Enrollment is currently unavailable; existing mapped fingerprints can start/end attendance by scan when a schedule window is active in an **active semester**.

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
| **TEACHER** | Done | Create/edit; employee #; device PIN; fingerprints; active/inactive (schedule eligibility) |
| **TEACHER_SEMESTER** | Done | Semesters + enroll/unenroll; schedules require enrollment |
| **REGULAR / MAKEUP** | Done | `schedule_kind`; Makeup preferred when overlapping; expand-on-create recurrence through semester end |
| **CLASSROOM** | Done | Rooms, capacity, target temperature |
| **BIOMETRIC_DEVICE** | Done | Generated `deviceId` identifies the device; assignment determines its room |
| **AC_UNIT** | Done | Room `ac_status` + Admin remote on/off; ESP polls `/status` (~30s) |
| **BIOMETRIC_EVENT** | Done | ESP32 / API enroll + scan → start/end class |
| **ATTENDANCE** session | Done | `Active` → `Completed`; school-local timestamps |
| **LATE** | Done | Time-in after `LateGraceMinutes` |
| **ABSENT** | Done | Schedule ended with no punch |
| **MISSING TIME-OUT** | Done | Open past end + grace → auto-close, AC off |
| **ON TIME** | Done | Stored as `OnTime` (extra vs original list) |
| **EARLY DISMISSAL** | Not yet | Not derived from punches |
| **OVERTIME MINUTES** | Not yet | Not derived from punches |
| **ON LEAVE** | Not yet | No leave workflow in the UI |
| **AC ON / OFF** | Done | Class start → Cooling; end / missing timeout → Off (+ temperature logs) |
| **ATTENDANCE_EXCEPTION** | Not yet | No exception table / workflow |
| **NOTIFICATION / SMS** | Not yet | Deferred by design |

**Live spine today:** Teacher → semester → Regular/Makeup schedule → room scanner → punch → attendance (On time / Late / Absent / Missing time-out) → AC on/off.

**Natural next:** early dismissal / overtime from punches → On Leave → exceptions → SMS.

### Semesters and schedule kinds

```
Teacher → TeacherSemester → RegularSchedule / MakeupClass → Classroom
```

- **Semesters** — Admin → Semesters: create date-ranged terms, activate/deactivate, enroll teachers.
- **Enrollment** — required in `teacher_semesters` before creating schedules for that semester.
- **Schedule kind** — `Regular` or `Makeup`; date must fall inside the semester; when both are in window, **Makeup is preferred** for biometric/dashboard start.
- **Recurring schedules** — Admin → Schedules → Add schedule: choose a start date, times, and a repeat pattern. Matching weekdays from the start date through the semester `end_date` are expanded into individual schedule rows (same subject/room/teacher/kind/times). Overlaps with existing teacher or classroom bookings are **skipped**; the toast reports how many were created vs skipped. Custom lets you pick Mon–Fri checkboxes. Makeup can still repeat, but the UI warns that makeups are usually one-off.
- **Move schedule** — each row has a move action to change that single class’s date and times (teacher/room/subject/kind unchanged). Rejects overlaps, dates outside the semester, and schedules with an active attendance session.
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

Session lifecycle remains `Active` → `Completed` (**one attendance row per schedule**). After time-out, in-window re-login (`ac_on`) reopens that row (clears time-out); the next logout (`ended`) writes a fresh time-out. Reconciliation runs every minute and also before scan / dashboard attendance reads; it turns AC Off when a room is Cooling and **no schedule is still in-window** for that room (even if the teacher has not logged out). If a back-to-back class for the same room is already in its window, Cooling stays on.

---

## Build and test

```powershell
dotnet build
dotnet run --launch-profile https
dotnet test .\tests\SmartClassAC.Tests\SmartClassAC.Tests.csproj
node --test .\tests\remember-username.test.js
```

The fingerprint preparation database test is skipped unless `SMARTCLASS_TEST_CONNECTION` is set to a MariaDB account permitted to create and drop a temporary database. It applies the real migrations in a uniquely named database and deletes that database afterward. Do not put credentials in the repository.

The device scan API can be exercised with Bruno; legacy enrollment remains disabled.

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

#### 2026-11-07 — Admin device management and startup settings

- Added **Admin → Devices** to register devices and manage room assignment, generated display name, target temperature, status polling interval, active state, and scan/enrollment permissions. Moved the fingerprint wipe control out of System settings into Devices.
- Added migrations `010_biometric_device_settings.sql`, `011_unassigned_biometric_devices.sql`, and `012_generated_device_ids.sql`; device IDs are generated independently of room names, and devices can remain unassigned.
- Active sessions prevent changing affected room assignments, deactivating devices, or disabling scans.
- `/api/device/status` supplies the managed settings; scan and enrollment permissions are enforced server-side.
- ESP32 firmware `1.4.6` uses the generated `deviceId` API field, displays the synced room, applies settings on startup/reconnect, and polls using the managed interval. Wi-Fi, server URL, and API key remain provisioned on the device.

#### 2026-09-27 — Admin remote AC on/off to classroom devices

- Admin **AC control**: per-room On/Off (`RemoteOn` → `Remote` / `RemoteOff` → `Off`).
- Devices: ESP32 firmware `1.2.12` polls `POST /api/device/status` with the 30s health check and applies `acOn` when it changes.
- Firmware `1.2.13`: a failed status sync no longer clears `serverOnline` (fixes idle LCD stuck on **No server** after remote control).
- Reconcile auto-off only clears session-tied `Cooling` (not `Remote` / `Override`).

#### 2026-09-27 — Device server health check

- ESP32 firmware `1.2.9` polls existing `GET /health` every 30s; Serial `HEALTH` forces a check.
- Idle LCD (`1.2.14+`): row 1 **Ready to scan**, row 2 **Offline** when unreachable; scans blocked until healthy.

#### 2026-09-27 — Device status recovery after power loss

- `POST /api/device/status` `{ deviceId }` returns room `acOn` / `acStatus`, `teacherDisplayName`, and `sessionActive`.
- ESP32 firmware `1.2.5` calls it after WiFi connect/reconnect (and Serial `STATUS`) to restore relay + LCD teacher name.

#### 2026-09-27 — LCD shows logged-in teacher while AC is on

- ESP32 firmware `1.2.4`: after `started` / `ac_on`, scanning idle screen shows teacher name + `AC ON`; cleared on `ended` / `ac_off`.

#### 2026-09-27 — Re-login updates attendance time-out

- In-window `ac_on` reopens the schedule’s attendance row (clears `time_out`, status Active).
- Following logout (`ended`) writes a new `time_out`; repeats for each re-login / logout in the window.

#### 2026-09-27 — In-window AC re-entry (no new attendance)

- After time-out for a schedule, further scans while still inside that schedule window reuse the same attendance row (`ac_on` / `ended`); a new attendance insert is not created.
- ESP32 firmware `1.2.3` shows “AC resumed” / “AC off” for those actions.
- Reconcile turns Cooling Off when the schedule window ends unless another in-window schedule still uses that room (logout not required).

#### 2026-09-27 — ESP32 firmware versioning

- Sketch tracks `FIRMWARE_VERSION` (semver) with in-file changelog; bump on every firmware change.
- Version shown on LCD splash, Serial boot, `HELP`, and `STATUS` (current `1.2.3`).
- App listens on `0.0.0.0:5062` so LAN scanners can reach the PC (not localhost-only).

#### 2026-09-27 — Document required workflow status

- README now maps the full Teacher → … → Attendance → AC / SMS domain flow with Done / Partial / Not yet statuses.

#### 2026-09-27 — School-local recorded timestamps

- Attendance, temperature logs now write and display school wall-clock time via `SchoolTime:TimeZoneId` (default `Singapore Standard Time`).
- Removed `ToLocalTime()` display conversions that shifted already-local / mislabeled UTC values.

#### 2026-09-27 — Remove request and support ticket pages

- Admin and Teacher dashboards no longer expose leave/substitute requests, early-out/overtime requests, or support tickets.
- Overview and navigation focus on classrooms, schedules, attendance, and AC control.

#### 2026-09-27 — Teacher mobile + registration OTP

- Teacher email is optional; mobile (`09xxxxxxxxx`) is required and unique.
- Migration `008` backfills missing phones and adds `uq_user_accounts_phone`.
- Admin create-teacher flow asks for OTP after the form (stub accepts **0000** for all registrations until real SMS delivery).
- Teacher profile edit enforces the same mobile format.

#### 2026-09-27 — Clear fingerprint devices from Settings

- Admin Settings can wipe all MariaDB fingerprint templates and queue every active scanner to clear its sensor.
- Migration `009`: `biometric_devices.pending_clear_fingerprints`.
- `POST /api/device/status` returns `clearFingerprints`; firmware `1.2.15+` wipes the sensor and acknowledges via `clear-session`.

#### 2026-09-27 — Teacher active / inactive status

- Teachers list shows Active/Inactive status with activate/deactivate toggle and list filters (default: Active).
- Only active teachers can be selected for schedules and semester enrollment; inactive teachers cannot sign in.

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
- Demo PINs 1001/1002/1003; Bruno collection; ESP32 JSON. Serial `ENROLL` is disabled in firmware `1.6.4`.

#### 2026-08-31 — Cursor IDE and local setup

- Initial README, `.vscode` launch/tasks, `scripts/setup-user-secrets.ps1`.

<!-- Template:
#### YYYY-MM-DD — Short title

- What changed and why.
- Migration or setup steps, if any.
-->
