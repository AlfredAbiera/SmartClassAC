-- A guard is an unresolved sensor write; it never grants permission to write.
CREATE TABLE IF NOT EXISTS fingerprint_enrollment_write_guards (
    preparation_id VARCHAR(36) NOT NULL PRIMARY KEY,
    operation_id VARCHAR(36) NOT NULL,
    started_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_fingerprint_write_operation (operation_id),
    CONSTRAINT fk_fingerprint_write_preparation
        FOREIGN KEY (preparation_id) REFERENCES fingerprint_enrollment_preparations(id)
) ENGINE=InnoDB;
