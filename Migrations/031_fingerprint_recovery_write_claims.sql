-- A reserved recovery attempt is not a write grant.
CREATE TABLE IF NOT EXISTS fingerprint_recovery_write_claims (
    preparation_id VARCHAR(36) NOT NULL PRIMARY KEY,
    guarded_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    recovery_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    claimed_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_recovery_write_claim_operation (recovery_operation_id),
    CONSTRAINT fk_recovery_write_claim_attempt FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_replacement_recovery_attempts(preparation_id)
) ENGINE=InnoDB;
