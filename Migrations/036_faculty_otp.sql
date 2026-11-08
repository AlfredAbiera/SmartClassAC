CREATE TABLE IF NOT EXISTS faculty_otp_phone_limits (
    phone_key BINARY(32) NOT NULL PRIMARY KEY,
    window_start_utc DATETIME(6) NOT NULL,
    request_count INT NOT NULL,
    KEY ix_faculty_otp_phone_window (window_start_utc)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS faculty_otp_device_limits (
    device_id BIGINT NOT NULL PRIMARY KEY,
    window_start_utc DATETIME(6) NOT NULL,
    request_count INT NOT NULL,
    CONSTRAINT fk_faculty_otp_limit_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS faculty_otp_requests (
    device_id BIGINT NOT NULL PRIMARY KEY,
    request_id BINARY(16) NOT NULL,
    teacher_id BIGINT NOT NULL,
    phone_key BINARY(32) NOT NULL,
    code_tag BINARY(32) NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    failed_attempts INT NOT NULL DEFAULT 0,
    delivery_accepted TINYINT(1) NOT NULL DEFAULT 0,
    CONSTRAINT fk_faculty_otp_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE,
    CONSTRAINT fk_faculty_otp_teacher FOREIGN KEY (teacher_id) REFERENCES user_accounts(id) ON DELETE CASCADE
) ENGINE=InnoDB;
