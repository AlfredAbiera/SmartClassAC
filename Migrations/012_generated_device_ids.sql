-- SmartClass AC / MariaDB
-- Version: 012
-- Replace room-derived device codes with stable, system-generated identifiers.

ALTER TABLE biometric_devices
    CHANGE COLUMN device_code device_id VARCHAR(64) NOT NULL;

ALTER TABLE biometric_devices
    DROP INDEX uq_biometric_devices_code,
    ADD CONSTRAINT uq_biometric_devices_id UNIQUE (device_id);

UPDATE biometric_devices
SET device_id = CONCAT('DEVICE-', id),
    display_name = CONCAT('Device ', id);
