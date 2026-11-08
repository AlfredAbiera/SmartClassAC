-- A fence retires the original write path but does not authorize recovery.
CREATE TABLE IF NOT EXISTS fingerprint_replacement_write_fences (
    preparation_id VARCHAR(36) NOT NULL PRIMARY KEY,
    operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    fenced_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_replacement_fenced_operation (operation_id),
    CONSTRAINT fk_replacement_write_fence_guard FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_enrollment_write_guards(preparation_id) ON DELETE CASCADE
) ENGINE=InnoDB;
