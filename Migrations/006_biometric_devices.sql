-- SmartClass AC / MariaDB
-- Version: 006
-- Per-classroom biometric devices (one fingerprint scanner per room).

CREATE TABLE IF NOT EXISTS biometric_devices (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    classroom_id BIGINT NOT NULL,
    device_code VARCHAR(64) NOT NULL,
    display_name VARCHAR(120) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    CONSTRAINT uq_biometric_devices_code UNIQUE (device_code),
    CONSTRAINT uq_biometric_devices_classroom UNIQUE (classroom_id),
    CONSTRAINT fk_biometric_devices_classroom
        FOREIGN KEY (classroom_id) REFERENCES classrooms(id) ON DELETE CASCADE,
    KEY ix_biometric_devices_active (is_active)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Seed one device per existing active classroom (code derived from room name).
INSERT INTO biometric_devices (classroom_id, device_code, display_name, is_active)
SELECT
    c.id,
    UPPER(REPLACE(c.name, ' ', '-')),
    CONCAT(c.name, ' scanner'),
    1
FROM classrooms c
WHERE c.is_active = TRUE
  AND NOT EXISTS (
      SELECT 1 FROM biometric_devices d WHERE d.classroom_id = c.id
  );
