ALTER TABLE attendance_logs
    ADD COLUMN IF NOT EXISTS missing_time_out_sms_status VARCHAR(16) NOT NULL DEFAULT 'NotRequired' AFTER outcome,
    ADD COLUMN IF NOT EXISTS missing_time_out_sms_attempted_utc DATETIME(6) NULL AFTER missing_time_out_sms_status;

CREATE INDEX IF NOT EXISTS ix_attendance_timeout_sms_pending
    ON attendance_logs (missing_time_out_sms_status, missing_time_out_sms_attempted_utc);
