-- A recovery intent does not authorize a scanner write or resolve a guard.
CREATE TABLE IF NOT EXISTS fingerprint_replacement_recovery_intents (
    preparation_id VARCHAR(36) NOT NULL PRIMARY KEY,
    guarded_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    recovery_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    authorized_by BIGINT NOT NULL,
    created_utc DATETIME(6) NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    UNIQUE KEY uq_replacement_recovery_operation (recovery_operation_id),
    CONSTRAINT fk_replacement_recovery_guard FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_enrollment_write_guards(preparation_id) ON DELETE CASCADE
) ENGINE=InnoDB;
