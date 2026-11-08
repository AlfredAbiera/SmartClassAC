-- Replacement receipts are distinct from ordinary enrollment completion receipts.
CREATE TABLE IF NOT EXISTS fingerprint_replacement_completions (
    operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    preparation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    device_id VARCHAR(23) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    template_identifier BIGINT NOT NULL,
    replacement_template_id BIGINT NOT NULL,
    completed_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_replacement_completed_preparation (preparation_id)
) ENGINE=InnoDB;
