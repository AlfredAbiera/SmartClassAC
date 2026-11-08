-- Retires one recovery attempt without authorizing another sensor write.
CREATE TABLE IF NOT EXISTS fingerprint_recovery_attempt_fences (
    preparation_id VARCHAR(36) NOT NULL,
    guarded_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    recovery_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    fenced_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (preparation_id, recovery_operation_id),
    UNIQUE KEY uq_recovery_attempt_fenced_operation (recovery_operation_id),
    CONSTRAINT fk_recovery_attempt_fence_guard FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_enrollment_write_guards(preparation_id) ON DELETE CASCADE
) ENGINE=InnoDB;
