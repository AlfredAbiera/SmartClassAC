-- Device bindings keep a newly finalized sensor slot from matching on another scanner.
CREATE TABLE IF NOT EXISTS fingerprint_template_device_bindings (
    fingerprint_template_id BIGINT NOT NULL PRIMARY KEY,
    device_id VARCHAR(23) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CONSTRAINT fk_fingerprint_binding_template
        FOREIGN KEY (fingerprint_template_id) REFERENCES fingerprint_templates(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Completion records make a lost response distinguishable from an uncommitted write.
CREATE TABLE IF NOT EXISTS fingerprint_enrollment_completions (
    operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    preparation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    device_id VARCHAR(23) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    completed_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_fingerprint_completed_preparation (preparation_id)
) ENGINE=InnoDB;
