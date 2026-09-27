-- SmartClass AC / MariaDB
-- Version: 009
-- Pending remote wipe of fingerprint sensor templates (Admin Settings → devices poll /status).

ALTER TABLE biometric_devices
    ADD COLUMN IF NOT EXISTS pending_clear_fingerprints TINYINT(1) NOT NULL DEFAULT 0
        AFTER is_active;
