ALTER TABLE attendance_logs
    ADD COLUMN IF NOT EXISTS session_generation BIGINT NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS attendance_otp_requests (
    device_id BIGINT NOT NULL PRIMARY KEY,
    admin_id BIGINT NOT NULL,
    schedule_id BIGINT NOT NULL,
    action_name VARCHAR(8) NOT NULL,
    binding VARCHAR(512) NOT NULL,
    CONSTRAINT fk_attendance_otp_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE,
    CONSTRAINT fk_attendance_otp_admin FOREIGN KEY (admin_id) REFERENCES user_accounts(id) ON DELETE CASCADE,
    CONSTRAINT fk_attendance_otp_schedule FOREIGN KEY (schedule_id) REFERENCES class_schedules(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS attendance_otp_events (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    admin_id BIGINT NOT NULL,
    teacher_id BIGINT NOT NULL,
    schedule_id BIGINT NOT NULL,
    device_id BIGINT NOT NULL,
    action_name VARCHAR(8) NOT NULL,
    recorded_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB;
