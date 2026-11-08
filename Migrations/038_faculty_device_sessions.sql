CREATE TABLE IF NOT EXISTS faculty_device_sessions (
    device_id BIGINT NOT NULL PRIMARY KEY,
    teacher_id BIGINT NOT NULL,
    token_hash BINARY(32) NOT NULL,
    phone_key BINARY(32) NOT NULL,
    paired_at DATETIME(6) NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    CONSTRAINT fk_faculty_session_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE,
    CONSTRAINT fk_faculty_session_teacher FOREIGN KEY (teacher_id) REFERENCES user_accounts(id) ON DELETE CASCADE
) ENGINE=InnoDB;
