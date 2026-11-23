ALTER TABLE attendance_logs
    ADD COLUMN IF NOT EXISTS class_extension_minutes INT NOT NULL DEFAULT 0 AFTER outcome;
