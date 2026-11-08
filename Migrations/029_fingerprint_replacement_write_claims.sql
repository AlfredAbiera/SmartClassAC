-- A claim is one-use and remains held until completion or a future safe recovery transition.
CREATE TABLE IF NOT EXISTS fingerprint_replacement_guard_authorizations (
    preparation_id VARCHAR(36) NOT NULL PRIMARY KEY,
    operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    authorized_by BIGINT NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    CONSTRAINT fk_replacement_guard_authorization FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_enrollment_write_guards(preparation_id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS fingerprint_replacement_write_claims (
    preparation_id VARCHAR(36) NOT NULL PRIMARY KEY,
    operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    claimed_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_replacement_write_claim_operation (operation_id),
    CONSTRAINT fk_replacement_write_claim_guard FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_enrollment_write_guards(preparation_id) ON DELETE CASCADE
) ENGINE=InnoDB;
