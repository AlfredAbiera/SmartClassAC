-- A preparation authorizes no sensor write. The block remains even if it is cancelled.
CREATE TABLE IF NOT EXISTS fingerprint_enrollment_preparations (
    id VARCHAR(36) NOT NULL PRIMARY KEY,
    device_id BIGINT NOT NULL,
    teacher_account_id BIGINT NOT NULL,
    finger_position VARCHAR(32) NOT NULL,
    template_identifier BIGINT NOT NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_preparation_device (device_id),
    UNIQUE KEY uq_preparation_slot (template_identifier),
    UNIQUE KEY uq_preparation_finger (teacher_account_id, finger_position),
    CONSTRAINT fk_preparation_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE,
    CONSTRAINT fk_preparation_teacher FOREIGN KEY (teacher_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE,
    CONSTRAINT fk_preparation_block FOREIGN KEY (template_identifier) REFERENCES fingerprint_slot_blocks(template_identifier)
) ENGINE=InnoDB;
