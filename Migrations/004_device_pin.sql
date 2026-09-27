-- SmartClass AC / MariaDB
-- Version: 004
-- Device PIN for fingerprint enroll/update on hardware.

ALTER TABLE user_accounts
    ADD COLUMN IF NOT EXISTS device_pin VARCHAR(6) NULL;

-- Backfill teachers missing a PIN (deterministic from id for existing rows).
UPDATE user_accounts
SET device_pin = LPAD(CAST((100000 + id) % 1000000 AS CHAR), 6, '0')
WHERE role = 'Teacher'
  AND is_active = TRUE
  AND (device_pin IS NULL OR device_pin = '');

CREATE UNIQUE INDEX IF NOT EXISTS uq_user_accounts_device_pin
    ON user_accounts (device_pin);
