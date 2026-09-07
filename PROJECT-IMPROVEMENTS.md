# SmartClass AC Improvements Roadmap

This document records the recommended improvements identified during the project review. Priorities are based on security exposure, correctness, operational risk, and impact on users.

## Priority Summary

| Priority | Meaning | Target |
|---|---|---|
| P0 | Security or correctness issue that should block production use | Fix before deployment or real users |
| P1 | High-impact reliability or business-logic issue | Fix in the next development cycle |
| P2 | Important usability, performance, and maintainability work | Fix after P0/P1 |
| P3 | Longer-term product and engineering improvements | Plan as capacity allows |

## Current Progress

Completed or partially completed since this roadmap was created:

- [x] **P0 #2 - Complete:** Encrypted ASP.NET Core cookie authentication now persists identity across refreshes and reconnects, with active-account revalidation and current-password confirmation for administrator credential changes and system resets. Automated coverage remains.
- [ ] **P1 #5 - Partial:** Active-class checks and a PostgreSQL unique index prevent multiple active classes per teacher. Full idempotent start/end behavior remains.
- [ ] **P1 #6 - Partial:** Class starts require the configured school-local date and schedule time window. [x] Time-zone configuration is now documented. [ ] Grace-period policy remains.
- [x] **P1 #7 - Complete:** Substitute IDs are validated as active teacher accounts, and schedule conflicts return safe operation errors.
- [x] **P1 #9 - Complete:** Database initialization retries with exponential backoff, failures are logged safely, and `/health` checks Supabase reachability.
- [ ] **P1 #10 - Partial:** Past leave dates are rejected; early-out and overtime requests are restricted to today. [x] Duplicate pending requests and approval of expired requests are now blocked. Remaining substitute and assignment workflow improvements remain.
- [x] **Additional security:** Login throttling locks an identifier for five minutes after five failed attempts.
- [x] **P3 #19 - Partial:** Added an xUnit test project with thirteen passing tests for password hashing, malformed hashes, password policy, and login throttling. Database-backed schedule, attendance, password-reset, and end-to-end workflow tests remain.
- [ ] **P2 #11/#12 - Partial:** Dashboard datasets now load concurrently, and temperature-log reads are limited in SQL. Full dashboard read models, pagination, filtering, sorting, and date ranges remain.

Items not listed above remain planned or incomplete.

## P0: Before Production

### 1. Remove exposed credentials

- Remove database hosts, passwords, and default credentials from `scripts/setup-user-secrets.ps1`.
- Rotate every credential that has previously been committed or shared.
- Keep local values in .NET User Secrets and deployment values in environment variables or a secret manager.
- Add secret scanning to CI.

### 2. Replace circuit-only authentication - Complete

`AuthService` stores the current user in a scoped service. This is tied to the Blazor circuit and is not a durable authentication session.

- Use ASP.NET Core cookie authentication or a server-side session mechanism.
- Persist authentication across refreshes and reconnects.
- Revalidate the user and account status during long-lived circuits.
- Test access to admin and teacher routes after reconnecting or opening a second browser session.

**Status:** Complete for durable cookie persistence, active-account revalidation, and reauthentication before sensitive administrator actions. Automated tests remain recommended.

### 3. Enforce authorization at the server boundary

The UI uses `[Authorize]`, but every sensitive repository operation should also be protected by a durable authenticated identity and explicit server-side authorization checks.

- Verify the current account, role, and active status for every command.
- Never trust IDs or role assumptions supplied by the UI.
- Require reauthentication for administrator credential changes and full system reset.

### 4. Remove fake security and device claims

Fingerprint template IDs are currently generated UUIDs, and temperature commands are database state changes rather than confirmed physical-device actions.

- Integrate the actual biometric and IoT systems, or label these workflows as demo/simulated features.
- Do not display `Biometric`, `Connected`, or `System online` as confirmed facts without a real health signal.

## P1: Correctness and Reliability

### 5. Prevent multiple active classes - Partial

A teacher can potentially start multiple classes through concurrent requests. The current start query checks only that the schedule belongs to the teacher and is scheduled for today.

- Add a database constraint or transaction-level check allowing only one active attendance record per teacher.
- Make start operations idempotent.
- Ensure ending a class targets the intended schedule rather than simply the latest open attendance record.

**Status:** Partial. The database constraint and start guard are implemented; idempotency and explicit end-target behavior remain.

### 6. Enforce schedule windows - Partial

Teachers can start a class at any time on its scheduled date.

- Require the current time to be within the schedule window.
- Define an explicit early-start and late-end grace period.
- Use a configured school time zone rather than server-local time.

**Status:** Partial. Date/time-window checks, configurable school time-zone support, and configuration documentation are implemented; grace-period policy remains.

### 7. Validate substitute assignments server-side - Complete

The UI filters substitute choices, but the repository should independently validate them.

- Confirm the substitute is an active teacher.
- Reject administrator, inactive, or nonexistent account IDs.
- Validate all affected schedules for conflicts.
- Handle database exclusion violations and return a user-friendly result.

**Status:** Complete.

### 8. Separate migrations from application startup

The repository creates tables, extensions, indexes, triggers, and constraints during every application startup.

- Move schema changes into versioned migrations or an explicit deployment script.
- Run migrations with a controlled deployment identity.
- Let the application start in a clear degraded state when the database is temporarily unavailable.

### 9. Add database retry and health handling - Complete

- Add bounded retry with exponential backoff for transient connection failures.
- Add database health checks and a `/health` endpoint.
- Log connection failures with structured context while excluding credentials.
- Show a useful maintenance or degraded-state message instead of an unhandled circuit exception.

**Status:** Complete for retries, logging, and health checks. A user-facing degraded database state remains.

### 10. Make request workflows complete - Partial

Add business rules for:

- duplicate leave or overtime requests
- requests for past dates
- approval after a schedule date has passed
- substitute availability across every affected schedule
- consistent preservation of original and substitute teacher assignments

**Status:** Partial. Past-date, request-type date, duplicate-pending-request, and expired-approval validation are implemented; remaining substitute and assignment workflow rules are pending.

## P2: Performance and User Experience

### 11. Optimize dashboard loading

Admin and teacher pages load many independent datasets sequentially.

- Create dashboard-specific read models or aggregate queries.
- Parallelize independent reads where appropriate.
- Cancel requests when a circuit is disconnected.
- Avoid reloading every dataset after a small change.

**Status:** Partial. Admin and teacher dashboard reads now execute concurrently; dashboard-specific read models and targeted refreshes remain.

### 12. Add server-side pagination and filtering

Attendance, temperature logs, support tickets, requests, teachers, and schedules currently have no complete pagination strategy.

- Add SQL `LIMIT`/`OFFSET` or keyset pagination.
- Add date-range filtering, status filtering, search, and sorting.
- Apply limits in SQL instead of loading all rows and then calling `Take()` in Razor.

**Status:** Partial. Temperature-log reads are bounded in SQL; complete pagination and filtering controls remain.

### 13. Standardize time handling

The UI mixes `DateTime.Today` and UTC timestamps.

- Configure one application time zone for the school.
- Store timestamps in UTC.
- Convert only at the display boundary.
- Use the configured zone for schedule windows and “today” calculations.

### 14. Improve form feedback

- Add field-level validation and accessible validation messages.
- Disable duplicate submissions while commands are running.
- Show clear success, warning, and failure states.
- Add confirmation details for shutdowns, resets, deletions, and password changes.

### 15. Improve accessibility and responsive behavior

- Keep `aria-expanded` synchronized with mobile navigation state.
- Add table captions and useful empty states.
- Improve keyboard focus management for dialogs.
- Return focus after closing modals.
- Add live-region announcements for asynchronous actions.
- Test dense tables and dashboard navigation at mobile widths.

## P3: Maintainability and Product Maturity

### 16. Split large Razor components

Break `Admin.razor` and `Teacher.razor` into reusable components for:

- navigation and top bars
- metric cards
- data tables
- request cards
- forms
- confirmation dialogs
- temperature controls

### 17. Replace magic strings

Use enums or constants for roles, request statuses, ticket statuses, attendance statuses, and temperature event types instead of repeated string literals.

### 18. Improve error handling and observability

- Replace empty `catch` blocks with structured logging and safe user messages.
- Define a consistent application error model.
- Include correlation IDs for administrative operations.
- Add audit history for logins, password changes, resets, schedule changes, deletions, requests, and temperature commands.

### 19. Add automated tests

Unit tests should cover:

- password hashing and malformed hashes
- password policy
- role authorization
- schedule overlap rules
- active-class rules
- substitute validation
- request approval
- reset-token expiry and single-use behavior

Integration and end-to-end tests should cover:

- first access
- admin login and logout
- teacher creation
- classroom and schedule management
- teacher class start/end
- request approval
- password reset
- system reset

**Status:** Partial. Authentication foundation tests are implemented and passing (13 tests); database-backed schedule, attendance, password-reset, and end-to-end workflow tests remain.

### 20. Add CI and deployment checks

- Build and test on every change.
- Run formatting and static analysis.
- Scan dependencies for vulnerabilities.
- Scan commits and configuration for secrets.
- Validate production configuration before deployment.
- Use a disposable PostgreSQL-compatible database for integration tests.

## Recommended Delivery Order

1. Rotate exposed credentials and remove secrets from source.
2. Implement durable authentication and server-side authorization.
3. Replace or clearly label simulated biometric and IoT behavior.
4. Enforce one active class, schedule windows, and substitute validity.
5. Separate migrations from startup and add health/retry handling.
6. Standardize time zones and complete request business rules.
7. Add pagination and optimize dashboard queries.
8. Improve form validation, accessibility, and responsive behavior.
9. Add unit, integration, and end-to-end tests.
10. Refactor large components and add CI/deployment automation.

## Definition of Done for Production

- No credentials are committed to the repository.
- Authentication survives refreshes and reconnects.
- Every privileged operation is authorized server-side.
- Device and biometric status reflects real integrations.
- Schedule, attendance, substitute, and request rules are enforced transactionally.
- Database migrations are versioned and observable.
- Health checks, structured logs, and safe error messages are available.
- Large datasets are paginated.
- Critical workflows have automated tests.
- Deployment checks pass without manual secret editing in source files.
