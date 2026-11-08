-- SmartClass AC / MariaDB
-- Version: 014
-- Numeric device IDs and device-side pairing. Devices must be paired from the
-- hardware (admin PIN + one-time pairing code) before the server accepts their ID.

ALTER TABLE biometric_devices
    ADD COLUMN IF NOT EXISTS pairing_code VARCHAR(6) NULL,
    ADD COLUMN IF NOT EXISTS paired_at DATETIME NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_biometric_devices_pairing_code
    ON biometric_devices (pairing_code);

-- Existing devices get numeric IDs and must be re-paired; generate their codes in the portal.
UPDATE biometric_devices
SET device_id = CAST(1000000000 + id AS CHAR),
    paired_at = NULL;
