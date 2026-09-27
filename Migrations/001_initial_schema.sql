-- SmartClass AC / MariaDB Initial Schema
-- Version: 001
-- Run through DatabaseMigrationRunner, MariaDB CLI, or phpMyAdmin.

CREATE TABLE IF NOT EXISTS schema_versions (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    version_key VARCHAR(100) NOT NULL,
    applied_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    script_name VARCHAR(255) NOT NULL,
    UNIQUE KEY uq_schema_versions_version_key (version_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS user_accounts (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(100) NOT NULL,
    display_name VARCHAR(160) NOT NULL,
    email VARCHAR(254) NULL,
    phone VARCHAR(40) NULL,
    employee_number VARCHAR(40) NULL,
    device_pin VARCHAR(6) NULL,
    password_hash VARCHAR(512) NOT NULL,
    role VARCHAR(16) NOT NULL,
    is_default_admin TINYINT(1) NOT NULL DEFAULT 0,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    is_default_admin_key TINYINT
        GENERATED ALWAYS AS (IF(is_default_admin, 1, NULL)) VIRTUAL,
    CONSTRAINT ck_user_accounts_role CHECK (role IN ('Admin', 'Teacher')),
    UNIQUE KEY uq_user_accounts_username (username),
    UNIQUE KEY uq_user_accounts_email (email),
    UNIQUE KEY uq_user_accounts_employee_number (employee_number),
    UNIQUE KEY uq_user_accounts_device_pin (device_pin),
    UNIQUE KEY uq_user_accounts_default_admin (is_default_admin_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS classrooms (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    capacity INT NOT NULL,
    target_temperature INT NOT NULL DEFAULT 22,
    current_temperature DECIMAL(4,1) NULL,
    ac_status VARCHAR(24) NOT NULL DEFAULT 'Idle',
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    CONSTRAINT ck_classrooms_capacity CHECK (capacity BETWEEN 1 AND 1000),
    CONSTRAINT ck_classrooms_target_temperature CHECK (target_temperature BETWEEN 16 AND 30),
    UNIQUE KEY uq_classrooms_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS fingerprint_templates (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    user_account_id BIGINT NOT NULL,
    finger_position VARCHAR(32) NOT NULL,
    template_identifier BIGINT NOT NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT uq_fingerprint_templates_identifier UNIQUE (template_identifier),
    CONSTRAINT uq_fingerprint_templates_user_finger UNIQUE (user_account_id, finger_position),
    CONSTRAINT fk_fingerprint_templates_user
        FOREIGN KEY (user_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS password_reset_tokens (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    user_account_id BIGINT NOT NULL,
    token_hash CHAR(64) NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    used_utc DATETIME(6) NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_password_reset_tokens_hash (token_hash),
    KEY ix_password_reset_tokens_account_expiry (user_account_id, expires_utc),
    CONSTRAINT fk_password_reset_tokens_user
        FOREIGN KEY (user_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS class_schedules (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    teacher_account_id BIGINT NOT NULL,
    original_teacher_account_id BIGINT NULL,
    classroom_id BIGINT NULL,
    classroom_name VARCHAR(100) NOT NULL,
    subject_name VARCHAR(160) NOT NULL DEFAULT 'Scheduled Class',
    schedule_date DATE NOT NULL,
    start_time TIME NOT NULL,
    end_time TIME NOT NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    CONSTRAINT ck_class_schedules_time_range CHECK (end_time > start_time),
    KEY ix_class_schedules_teacher_date (teacher_account_id, schedule_date, start_time),
    KEY ix_class_schedules_classroom_date (classroom_name, schedule_date, start_time),
    CONSTRAINT fk_class_schedules_teacher
        FOREIGN KEY (teacher_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE,
    CONSTRAINT fk_class_schedules_original_teacher
        FOREIGN KEY (original_teacher_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE,
    CONSTRAINT fk_class_schedules_classroom
        FOREIGN KEY (classroom_id) REFERENCES classrooms(id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS attendance_logs (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    class_schedule_id BIGINT NULL,
    teacher_account_id BIGINT NOT NULL,
    classroom_id BIGINT NOT NULL,
    time_in_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    time_out_utc DATETIME(6) NULL,
    verification_method VARCHAR(40) NOT NULL DEFAULT 'Biometric',
    status VARCHAR(24) NOT NULL DEFAULT 'Active',
    active_session_key BIGINT
        GENERATED ALWAYS AS (IF(time_out_utc IS NULL, teacher_account_id, NULL)) VIRTUAL,
    CONSTRAINT ck_attendance_logs_status CHECK (status IN ('Active', 'Completed')),
    UNIQUE KEY uq_attendance_logs_schedule (class_schedule_id),
    UNIQUE KEY uq_attendance_logs_one_active_teacher (active_session_key),
    KEY ix_attendance_logs_teacher_time (teacher_account_id, time_in_utc),
    CONSTRAINT fk_attendance_logs_schedule
        FOREIGN KEY (class_schedule_id) REFERENCES class_schedules(id) ON DELETE SET NULL,
    CONSTRAINT fk_attendance_logs_teacher
        FOREIGN KEY (teacher_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE,
    CONSTRAINT fk_attendance_logs_classroom
        FOREIGN KEY (classroom_id) REFERENCES classrooms(id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS teacher_requests (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    teacher_account_id BIGINT NOT NULL,
    request_type VARCHAR(16) NOT NULL,
    request_date DATE NOT NULL,
    reason VARCHAR(1000) NOT NULL,
    needs_substitute TINYINT(1) NOT NULL DEFAULT 0,
    substitute_teacher_account_id BIGINT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'Pending',
    admin_response VARCHAR(1000) NULL,
    reviewed_by_account_id BIGINT NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    reviewed_utc DATETIME(6) NULL,
    pending_day_key VARCHAR(80)
        GENERATED ALWAYS AS (
            IF(status = 'Pending',
               CONCAT(teacher_account_id, ':', request_type, ':', request_date),
               NULL)
        ) VIRTUAL,
    CONSTRAINT ck_teacher_requests_type CHECK (request_type IN ('Leave', 'EarlyOut', 'Overtime')),
    CONSTRAINT ck_teacher_requests_status CHECK (status IN ('Pending', 'Approved', 'Rejected')),
    UNIQUE KEY uq_teacher_requests_pending_day (pending_day_key),
    KEY ix_teacher_requests_status (status, created_utc),
    CONSTRAINT fk_teacher_requests_teacher
        FOREIGN KEY (teacher_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE,
    CONSTRAINT fk_teacher_requests_substitute
        FOREIGN KEY (substitute_teacher_account_id) REFERENCES user_accounts(id) ON DELETE SET NULL,
    CONSTRAINT fk_teacher_requests_reviewer
        FOREIGN KEY (reviewed_by_account_id) REFERENCES user_accounts(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS support_tickets (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    teacher_account_id BIGINT NOT NULL,
    category VARCHAR(100) NOT NULL,
    details VARCHAR(2000) NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'Open',
    admin_response VARCHAR(2000) NULL,
    reviewed_by_account_id BIGINT NULL,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    resolved_utc DATETIME(6) NULL,
    CONSTRAINT ck_support_tickets_status CHECK (status IN ('Open', 'Resolved', 'Dismissed')),
    KEY ix_support_tickets_status (status, created_utc),
    CONSTRAINT fk_support_tickets_teacher
        FOREIGN KEY (teacher_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE,
    CONSTRAINT fk_support_tickets_reviewer
        FOREIGN KEY (reviewed_by_account_id) REFERENCES user_accounts(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS temperature_logs (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    classroom_id BIGINT NOT NULL,
    actor_account_id BIGINT NULL,
    measured_temperature DECIMAL(4,1) NULL,
    target_temperature INT NULL,
    event_type VARCHAR(32) NOT NULL,
    notes VARCHAR(1000) NULL,
    recorded_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT ck_temperature_logs_target CHECK (
        target_temperature IS NULL OR target_temperature BETWEEN 16 AND 30
    ),
    KEY ix_temperature_logs_classroom_time (classroom_id, recorded_utc),
    CONSTRAINT fk_temperature_logs_classroom
        FOREIGN KEY (classroom_id) REFERENCES classrooms(id) ON DELETE CASCADE,
    CONSTRAINT fk_temperature_logs_actor
        FOREIGN KEY (actor_account_id) REFERENCES user_accounts(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
