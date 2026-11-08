-- A reserved attempt is not permission to write and must not remove the guard.
CREATE TABLE IF NOT EXISTS fingerprint_replacement_recovery_attempts (
    preparation_id VARCHAR(36) NOT NULL PRIMARY KEY,
    guarded_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    recovery_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    reserved_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_recovery_attempt_operation (recovery_operation_id),
    CONSTRAINT fk_recovery_attempt_guard FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_enrollment_write_guards(preparation_id)
) ENGINE=InnoDB;
