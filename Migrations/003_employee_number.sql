-- SmartClass AC / MariaDB
-- Version: 003
-- Add employee number to teacher/user profile records.

ALTER TABLE user_accounts
    ADD COLUMN IF NOT EXISTS employee_number VARCHAR(40) NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_user_accounts_employee_number
    ON user_accounts (employee_number);
