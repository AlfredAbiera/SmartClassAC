CREATE TABLE IF NOT EXISTS fingerprint_deletion_requests (
    id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    device_id BIGINT NOT NULL,
    fingerprint_template_id BIGINT NOT NULL,
    teacher_account_id BIGINT NOT NULL,
    template_identifier BIGINT NOT NULL,
    requested_by BIGINT NOT NULL,
    state VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'Pending',
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_fingerprint_delete_device (device_id),
    UNIQUE KEY uq_fingerprint_delete_template (fingerprint_template_id),
    UNIQUE KEY uq_fingerprint_delete_slot (template_identifier),
    CONSTRAINT fk_fingerprint_delete_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id),
    CONSTRAINT fk_fingerprint_delete_template FOREIGN KEY (fingerprint_template_id) REFERENCES fingerprint_templates(id),
    CONSTRAINT fk_fingerprint_delete_teacher FOREIGN KEY (teacher_account_id) REFERENCES user_accounts(id),
    CONSTRAINT fk_fingerprint_delete_admin FOREIGN KEY (requested_by) REFERENCES user_accounts(id),
    CONSTRAINT fk_fingerprint_delete_block FOREIGN KEY (template_identifier) REFERENCES fingerprint_slot_blocks(template_identifier),
    CONSTRAINT ck_fingerprint_delete_state CHECK (state IN ('Pending', 'Claimed'))
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS fingerprint_deletion_completions (
    id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    device_id VARCHAR(23) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    template_identifier BIGINT NOT NULL,
    completed_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB;
