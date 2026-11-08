-- Challenges authenticate individual future protocol messages, not sensor writes.
CREATE TABLE IF NOT EXISTS fingerprint_enrollment_challenges (
    challenge_hash BINARY(32) NOT NULL PRIMARY KEY,
    device_id BIGINT NOT NULL,
    operation_name VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    KEY ix_enrollment_challenge_expiry (device_id, expires_utc),
    CONSTRAINT fk_enrollment_challenge_device
        FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE
) ENGINE=InnoDB;
