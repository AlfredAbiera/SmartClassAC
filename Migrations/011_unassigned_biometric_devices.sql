-- SmartClass AC / MariaDB
-- Version: 011
-- Allow devices to be registered without a room and keep stable Device N names.

UPDATE biometric_devices
SET display_name = CONCAT('Device ', id);

ALTER TABLE biometric_devices
    MODIFY COLUMN classroom_id BIGINT NULL;
