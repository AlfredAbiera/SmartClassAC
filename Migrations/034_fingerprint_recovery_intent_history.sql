-- Retain issued identities while their original guard is unresolved.
CREATE TABLE IF NOT EXISTS fingerprint_recovery_intent_history (
    preparation_id VARCHAR(36) NOT NULL,
    guarded_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    recovery_operation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    issued_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (preparation_id, recovery_operation_id),
    UNIQUE KEY uq_recovery_intent_history_operation (recovery_operation_id),
    CONSTRAINT fk_recovery_intent_history_guard FOREIGN KEY (preparation_id)
        REFERENCES fingerprint_enrollment_write_guards(preparation_id) ON DELETE CASCADE
) ENGINE=InnoDB;

INSERT INTO fingerprint_recovery_intent_history
    (preparation_id, guarded_operation_id, recovery_operation_id, issued_utc)
SELECT i.preparation_id, i.guarded_operation_id, i.recovery_operation_id, i.created_utc
FROM fingerprint_replacement_recovery_intents i
WHERE NOT EXISTS (
    SELECT 1 FROM fingerprint_recovery_intent_history h
    WHERE h.preparation_id = i.preparation_id AND h.guarded_operation_id = i.guarded_operation_id
      AND h.recovery_operation_id = i.recovery_operation_id
);
