-- An administrator's short-lived consent is not by itself permission to write.
CREATE TABLE IF NOT EXISTS fingerprint_enrollment_authorizations (
    preparation_id VARCHAR(36) NOT NULL PRIMARY KEY,
    operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    authorized_by BIGINT NOT NULL,
    allow_occupied_slot BOOLEAN NOT NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    expires_utc DATETIME(6) NOT NULL,
    UNIQUE KEY uq_enrollment_authorization_operation (operation_id),
    CONSTRAINT fk_enrollment_authorization_preparation FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_enrollment_preparations(id) ON DELETE CASCADE
) ENGINE=InnoDB;
