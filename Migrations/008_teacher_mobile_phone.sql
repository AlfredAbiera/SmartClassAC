-- SmartClass AC / MariaDB
-- Version: 008
-- Unique teacher mobile numbers (09xxxxxxxxx); backfill existing teachers missing phone.

-- Backfill teachers missing a mobile (deterministic 09 + 9-digit id pad).
UPDATE user_accounts
SET phone = CONCAT('09', LPAD(CAST(id AS CHAR), 9, '0'))
WHERE role = 'Teacher'
  AND (phone IS NULL OR phone = '');

CREATE UNIQUE INDEX IF NOT EXISTS uq_user_accounts_phone
    ON user_accounts (phone);
