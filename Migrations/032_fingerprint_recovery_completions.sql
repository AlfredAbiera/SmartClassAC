CREATE TABLE IF NOT EXISTS fingerprint_recovery_completions (
    recovery_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    guarded_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    preparation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    device_id VARCHAR(23) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    template_identifier BIGINT NOT NULL,
    replacement_template_id BIGINT NOT NULL,
    completed_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_recovery_completed_preparation (preparation_id)
) ENGINE=InnoDB;
