-- Store one administrator-selected enrollment scanner. Synchronization is not enabled by this setting.
CREATE TABLE IF NOT EXISTS fingerprint_enrollment_scanner (
    singleton_id TINYINT NOT NULL PRIMARY KEY,
    device_id BIGINT NOT NULL,
    CONSTRAINT ck_enrollment_scanner_singleton CHECK (singleton_id = 1),
    CONSTRAINT fk_enrollment_scanner_device
        FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE
) ENGINE=InnoDB;
