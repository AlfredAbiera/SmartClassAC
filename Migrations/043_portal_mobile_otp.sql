CREATE TABLE IF NOT EXISTS portal_mobile_otp_limits (
    scope_key BINARY(32) NOT NULL PRIMARY KEY,
    window_start_utc DATETIME(6) NOT NULL,
    request_count INT NOT NULL,
    KEY ix_portal_mobile_otp_limit_window (window_start_utc)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS portal_mobile_otp_requests (
    actor_id BIGINT NOT NULL,
    purpose VARCHAR(16) NOT NULL,
    target_id BIGINT NOT NULL,
    request_id BINARY(16) NOT NULL,
    phone_key BINARY(32) NOT NULL,
    code_tag BINARY(32) NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    failed_attempts INT NOT NULL DEFAULT 0,
    delivery_accepted BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (actor_id, purpose, target_id),
    CONSTRAINT fk_portal_mobile_otp_actor FOREIGN KEY (actor_id) REFERENCES user_accounts(id) ON DELETE CASCADE
) ENGINE=InnoDB;
