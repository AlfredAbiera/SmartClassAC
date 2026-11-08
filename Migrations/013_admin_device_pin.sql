-- SmartClass AC / MariaDB
-- Version: 013
-- Hashed administrator PIN used to unlock admin options on the scanner hardware.

ALTER TABLE user_accounts
    ADD COLUMN IF NOT EXISTS admin_device_pin_hash VARCHAR(255) NULL;
