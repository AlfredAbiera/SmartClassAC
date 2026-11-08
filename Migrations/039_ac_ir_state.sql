-- Durable, fail-closed state for relative IR commands. No transmission routes are enabled by this migration.
CREATE TABLE IF NOT EXISTS ac_ir_boots (
    device_id BIGINT NOT NULL,
    boot_id BINARY(32) NOT NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (device_id, boot_id),
    CONSTRAINT fk_ac_ir_boot_device FOREIGN KEY (device_id)
        REFERENCES biometric_devices(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS ac_ir_reconciliations (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    device_id BIGINT NOT NULL,
    classroom_id BIGINT NOT NULL,
    admin_account_id BIGINT NULL,
    boot_id BINARY(32) NOT NULL,
    target_temperature INT NOT NULL,
    confirmed_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT ck_ac_ir_reconciliation_target CHECK (target_temperature BETWEEN 16 AND 30),
    CONSTRAINT fk_ac_ir_reconciliation_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE,
    CONSTRAINT fk_ac_ir_reconciliation_room FOREIGN KEY (classroom_id) REFERENCES classrooms(id) ON DELETE CASCADE,
    CONSTRAINT fk_ac_ir_reconciliation_admin FOREIGN KEY (admin_account_id) REFERENCES user_accounts(id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS ac_ir_state (
    device_id BIGINT NOT NULL PRIMARY KEY,
    classroom_id BIGINT NOT NULL,
    paired_at DATETIME(6) NOT NULL,
    boot_id BINARY(32) NOT NULL,
    state VARCHAR(16) NOT NULL,
    assumed_temperature INT NULL,
    command_id BINARY(16) NULL,
    command_temperature INT NULL,
    last_seen_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    CONSTRAINT ck_ac_ir_state CHECK (state IN ('reconcile', 'ready', 'inflight')),
    CONSTRAINT ck_ac_ir_assumed CHECK (assumed_temperature IS NULL OR assumed_temperature BETWEEN 16 AND 30),
    CONSTRAINT ck_ac_ir_command_temperature CHECK (command_temperature IS NULL OR command_temperature BETWEEN 16 AND 30),
    CONSTRAINT fk_ac_ir_device FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE,
    CONSTRAINT fk_ac_ir_classroom FOREIGN KEY (classroom_id) REFERENCES classrooms(id) ON DELETE CASCADE
) ENGINE=InnoDB;
