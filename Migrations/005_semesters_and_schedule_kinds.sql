-- SmartClass AC / MariaDB
-- Version: 005
-- Semesters, teacher enrollment, and Regular/Makeup schedule kinds.

CREATE TABLE IF NOT EXISTS semesters (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(120) NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    created_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    CONSTRAINT ck_semesters_date_range CHECK (end_date >= start_date),
    UNIQUE KEY uq_semesters_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS teacher_semesters (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    teacher_account_id BIGINT NOT NULL,
    semester_id BIGINT NOT NULL,
    enrolled_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT uq_teacher_semesters UNIQUE (teacher_account_id, semester_id),
    CONSTRAINT fk_teacher_semesters_teacher
        FOREIGN KEY (teacher_account_id) REFERENCES user_accounts(id) ON DELETE CASCADE,
    CONSTRAINT fk_teacher_semesters_semester
        FOREIGN KEY (semester_id) REFERENCES semesters(id) ON DELETE CASCADE,
    KEY ix_teacher_semesters_semester (semester_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

ALTER TABLE class_schedules
    ADD COLUMN IF NOT EXISTS semester_id BIGINT NULL,
    ADD COLUMN IF NOT EXISTS schedule_kind VARCHAR(16) NOT NULL DEFAULT 'Regular';

-- Backfill: create a default open semester covering existing schedule dates (or today ± 6 months).
INSERT INTO semesters (name, start_date, end_date, is_active)
SELECT
    'Default Semester',
    DATE_SUB(COALESCE((SELECT MIN(schedule_date) FROM class_schedules), CURRENT_DATE), INTERVAL 14 DAY),
    DATE_ADD(COALESCE((SELECT MAX(schedule_date) FROM class_schedules), CURRENT_DATE), INTERVAL 180 DAY),
    1
WHERE NOT EXISTS (SELECT 1 FROM semesters LIMIT 1);

UPDATE class_schedules cs
INNER JOIN semesters s ON s.name = 'Default Semester'
SET cs.semester_id = s.id
WHERE cs.semester_id IS NULL;

UPDATE class_schedules
SET schedule_kind = 'Regular'
WHERE schedule_kind IS NULL OR schedule_kind = '';

-- Enroll every active teacher into every semester they have schedules for (or the default).
INSERT IGNORE INTO teacher_semesters (teacher_account_id, semester_id)
SELECT DISTINCT cs.teacher_account_id, cs.semester_id
FROM class_schedules cs
WHERE cs.semester_id IS NOT NULL;

INSERT IGNORE INTO teacher_semesters (teacher_account_id, semester_id)
SELECT u.id, s.id
FROM user_accounts u
CROSS JOIN semesters s
WHERE u.role = 'Teacher' AND u.is_active = TRUE AND s.name = 'Default Semester';

ALTER TABLE class_schedules
    MODIFY COLUMN schedule_kind VARCHAR(16) NOT NULL DEFAULT 'Regular';

-- Add FK after backfill (ignore if already present via failed re-run — MariaDB lacks IF NOT EXISTS for FK).
-- Application expects these constraints on fresh applies after backfill.

SET @fk_semester := (
    SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
    WHERE CONSTRAINT_SCHEMA = DATABASE()
      AND TABLE_NAME = 'class_schedules'
      AND CONSTRAINT_NAME = 'fk_class_schedules_semester'
);
SET @sql_fk := IF(@fk_semester = 0,
    'ALTER TABLE class_schedules ADD CONSTRAINT fk_class_schedules_semester FOREIGN KEY (semester_id) REFERENCES semesters(id) ON DELETE RESTRICT',
    'SELECT 1');
PREPARE stmt_fk FROM @sql_fk;
EXECUTE stmt_fk;
DEALLOCATE PREPARE stmt_fk;

SET @ck_kind := (
    SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
    WHERE CONSTRAINT_SCHEMA = DATABASE()
      AND TABLE_NAME = 'class_schedules'
      AND CONSTRAINT_NAME = 'ck_class_schedules_kind'
);
SET @sql_ck := IF(@ck_kind = 0,
    'ALTER TABLE class_schedules ADD CONSTRAINT ck_class_schedules_kind CHECK (schedule_kind IN (''Regular'', ''Makeup''))',
    'SELECT 1');
PREPARE stmt_ck FROM @sql_ck;
EXECUTE stmt_ck;
DEALLOCATE PREPARE stmt_ck;
