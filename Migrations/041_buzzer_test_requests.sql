CREATE TABLE IF NOT EXISTS buzzer_test_requests (
    device_id BIGINT NOT NULL PRIMARY KEY,
    paired_at DATETIME(6) NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    CONSTRAINT fk_buzzer_test_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE
) ENGINE=InnoDB;
