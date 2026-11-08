-- SmartClass AC / MariaDB
-- Version: 010
-- Device settings downloaded from the authenticated status endpoint.

ALTER TABLE biometric_devices
    ADD COLUMN IF NOT EXISTS status_poll_interval_seconds SMALLINT UNSIGNED NOT NULL DEFAULT 30
        AFTER pending_clear_fingerprints,
    ADD COLUMN IF NOT EXISTS fingerprint_enrollment_enabled TINYINT(1) NOT NULL DEFAULT 1
        AFTER status_poll_interval_seconds,
    ADD COLUMN IF NOT EXISTS attendance_scan_enabled TINYINT(1) NOT NULL DEFAULT 1
        AFTER fingerprint_enrollment_enabled;
