-- Authenticated sensor index snapshots are observations, never write authorization.
CREATE TABLE IF NOT EXISTS fingerprint_sensor_inventory (
    device_id BIGINT NOT NULL PRIMARY KEY,
    capacity SMALLINT UNSIGNED NOT NULL,
    bitmap VARBINARY(256) NOT NULL,
    observed_after_utc DATETIME(6) NOT NULL,
    received_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT fk_fingerprint_inventory_device
        FOREIGN KEY (device_id) REFERENCES biometric_devices(id) ON DELETE CASCADE
) ENGINE=InnoDB;
