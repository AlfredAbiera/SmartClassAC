-- Device keys are encrypted with an independently configured server key.
CREATE TABLE IF NOT EXISTS fingerprint_enrollment_master_key_check (
    singleton_id TINYINT NOT NULL PRIMARY KEY,
    key_check BINARY(32) NOT NULL,
    CONSTRAINT ck_enrollment_master_key_singleton CHECK (singleton_id = 1)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS fingerprint_enrollment_device_credentials (
    device_id BIGINT NOT NULL PRIMARY KEY,
    master_key_id TINYINT NOT NULL DEFAULT 1,
    protected_key VARBINARY(61) NOT NULL,
    key_fingerprint BINARY(32) NOT NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_enrollment_device_key_fingerprint (key_fingerprint),
    CONSTRAINT ck_enrollment_credential_master_singleton CHECK (master_key_id = 1),
    CONSTRAINT fk_enrollment_credential_master
        FOREIGN KEY (master_key_id) REFERENCES fingerprint_enrollment_master_key_check(singleton_id),
    CONSTRAINT fk_enrollment_credential_device
        FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE
) ENGINE=InnoDB;
