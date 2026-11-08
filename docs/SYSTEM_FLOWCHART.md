# SmartClass AC: comprehensive system flowchart

These diagrams describe the repository's portal and companion scanner source,
not proof that a particular deployment or device has been updated or tested.
Each diagram continues the system overview at a workflow boundary. Mermaid
rendering is supported by many Markdown viewers. See the [portal flow](./PORTAL_PROCESS_FLOW.md),
[firmware flow](./FIRMWARE_PROCESS_FLOW.md), [data model](./DATA_MODEL.md), and
[README](../README.md) for details, requirements, and limitations.

## 1. System overview

```mermaid
flowchart LR
    Admin[Administrator] --> Portal[Blazor portal and device API]
    Teacher[Teacher] --> Portal
    Scanner[ESP32 scanner] <-->|Device API| Portal
    Portal --> DB[(MariaDB)]
    Portal --> Modem[Server GSM modem]
    Modem --> Phone[Mobile phone]
    Portal --> SMTP[SMTP password recovery]
    Scanner --> Sensor[Fingerprint sensor]
    Scanner --> Buzzer[Buzzer]
    Scanner -->|Optional IR| AC[Physical AC]
    Scanner -->|Optional relay| Relay[Relay hardware]
```

## 2. Startup and access

```mermaid
flowchart TD
    Start[Start portal] --> Config[Load environment and protected configuration]
    Config --> Init[Initialize database and run migrations]
    Init -->|Success| First{First access required?}
    Init -->|Repeated failure| Degraded[Log error; portal may run degraded]
    First -->|Yes| Setup[Administrator configures recovery email]
    First -->|No| Login[Sign in]
    Setup --> Login
    Login -->|Valid credentials| Role{Role}
    Login -->|Invalid| Denied[Reject; apply login rate limits]
    Role -->|Admin| Admin[Administrator dashboard]
    Role -->|Teacher| Teacher[Teacher dashboard]
    Login -->|Forgot password| Reset[Email one-use expiring reset token]
    Reset --> Login
```

## 3. Teacher registration and mobile verification

```mermaid
flowchart TD
    Action[Admin adds teacher or admin/teacher changes mobile] --> Changed{New registration or changed number?}
    Changed -->|No| SaveOther[Save permitted non-phone edits]
    Changed -->|Yes| Validate[Validate role and proposed mobile]
    Validate --> Limit{Within OTP request limits?}
    Limit -->|No| Block[Do not save; show error]
    Limit -->|Yes| Reserve[Create actor-, purpose-, target- and phone-bound challenge]
    Reserve --> Send[Send six-digit code through server GSM modem]
    Send -->|Rejected, unavailable, or uncertain| Block
    Send -->|Modem accepts submission| Enter[Enter code within five minutes]
    Enter --> Verify{Valid, unexpired, within attempt limit?}
    Verify -->|No| Block
    Verify -->|Yes| Commit[Consume code and save account change in one transaction]
    Commit --> Done[Account updated]
```

`FacultyOtp:Secret` and a configured GSM modem are required. Modem acceptance
is not proof of delivery. Existing numbers are not retroactively verified;
demo seeding does not use this portal workflow.

## 4. Academic setup, scanner provisioning and pairing

```mermaid
flowchart TD
    Admin[Admin] --> Teachers[Manage teachers and active status]
    Admin --> Semester[Create semester and enroll teachers]
    Admin --> Rooms[Create rooms and temperature limits]
    Teachers --> Schedule[Create regular or makeup schedules]
    Semester --> Schedule
    Rooms --> Schedule
    Admin --> Device[Register Device N and set room and device permissions]
    Device --> PairCode[Generate pairing code; set device admin PIN]
    PairCode --> Pair[Scanner admin menu: PIN and pairing code]
    Pair -->|Accepted| Paired[Save assigned device ID on scanner]
    Pair -->|Denied| PairError[Correct problem or request a new code]
    Paired --> Sync[Fetch status, room, and settings]
```

Sensitive scanner protocols also require the per-device key, corresponding
server feature flags, and HTTPS outside Development.

## 5. Guarded fingerprint enrollment

```mermaid
flowchart TD
    Inventory[Enabled scanner reads sensor inventory] --> Report[Authenticate and report current sensor bitmap]
    Report --> Reserve[Admin selects teacher and finger; reserve eligible empty slot]
    Reserve --> Consent[Confirm short-lived consent for exact preparation]
    Consent --> Scan[Scanner admin starts enrollment]
    Scan --> Check[Check recovery state and sensor slot; capture fingerprint]
    Check --> Claim[Authenticate claim; server creates durable write guard]
    Claim --> Journal[Persist uncertain-write journal before writing]
    Journal --> Write[Write sensor and check acknowledgement]
    Write --> Finish[Authenticate completion and commit scanner-bound mapping]
    Finish --> Receipt[Verify signed receipt and clear journal]
    Check -->|Ineligible, occupied, or failure| Stop[Stop; do not write]
    Claim -->|Denied| Stop
    Write -->|Uncertain| Hold[Keep journal, guard and slot blocked; investigate]
    Finish -->|Uncertain| Hold
```

Enrollment depends on enabled scanner build flags, portal gates and keys.
Inventory expires and is invalidated on protected operations. Same-slot
replacement has separate gates and incomplete recovery limitations; never
clear a guard or block simply to retry.

## 6. Attendance and room demand

```mermaid
flowchart TD
    Scan[Scanner matches a finger] --> Validate[Server validates scanner, room, teacher mapping and eligibility]
    Validate -->|Invalid| Reject[Show denial]
    Validate -->|Active class in this room| End[Complete attendance and record time-out]
    Validate -->|No active class, eligible first scan| Begin[Start attendance; request cooling]
    Validate -->|Eligible in-window re-entry| Resume[Reopen prior attendance; request cooling]
    Validate -->|No eligible schedule| Reject
    End --> Other{Different class active or in its schedule window?}
    Other -->|Yes| Keep[Keep attendance power requested ON]
    Other -->|No| Off[Request attendance power OFF]
    Auto[Periodic reconciliation] -->|Missing time-out| Close[Close session and apply overlap rule]
    Close --> Other
    Auto -->|No punch after schedule| Absent[Record absence]
    Teacher[Teacher portal] -->|Start eligible class| Begin
    Teacher -->|End active class| End
```

The ending class's own remaining window does not keep its AC on after early
logout. Portal room status is not a measurement of the actual AC.

## 7. IR synchronization and attendance power

```mermaid
flowchart TD
    Boot[Scanner boots and registers IR boot] --> Reconcile[Admin physically verifies target and, for v2, ON/OFF]
    Reconcile --> Ready[Store assumptions for this boot and room]
    Ready --> Poll[Scanner polls on normal scanning screen]
    Poll --> Desired{Power differs from attendance request?}
    Desired -->|Yes, v2 enabled| ClaimPower[Claim unique in-flight power toggle]
    ClaimPower --> SendPower[Send one NEC power toggle]
    Desired -->|No| Temp{Temperature step needed and allowed?}
    Temp -->|Yes| ClaimTemp[Claim one relative temperature step]
    ClaimTemp --> SendTemp[Send NEC up or down]
    Temp -->|No| Poll
    SendPower --> Ack[Authenticated completion]
    SendTemp --> Ack
    Ack -->|Confirmed| Ready
    Ack -->|Uncertain| Halt[Halt IR; restart and physically reconcile]
    Poll -->|Uncertain claim| Halt
```

IR uses separate portal and scanner gates; enabling portal power control
disables legacy IR routes. A power command is a **toggle**, not distinct ON
and OFF codes. A diagnostic receiver or acknowledgment cannot confirm that the
AC received the signal. Manual remote use and missed signals can cause drift.
Remote and Override controls do not request IR power toggles.

## 8. Faculty menu, reminders, reports and maintenance

```mermaid
flowchart TD
    Menu[Scanner Faculty menu] --> OTP[Request and verify scanner-bound SMS OTP]
    OTP -->|Verified| Session[Short-lived Faculty session]
    Session --> Schedules[View today's schedules]
    Session --> Target[View or change permitted room target]
    Target --> Exit[Exit menu so scanning-screen IR polling can resume]
    Reminder[Server checks active class and reminder window] --> Poll[Scanner polls reminder or queued test]
    Poll --> Tone[Play five-second melody when claimed]
    Admin[Admin] --> Report[Filter attendance report]
    Report --> Export[Browser print/PDF or Excel export]
    Admin --> Settings[Manage GSM, buzzer, device PIN and system settings]
    Settings -->|Explicit confirmation| Test[Test SMS or buzzer]
    Settings -->|Explicit confirmation| Reset[Destructive reset or sensor clear, subject to guards]
```

Reminder and test claims do not prove sound played. Device clear and reset are
destructive operations, not troubleshooting shortcuts.
