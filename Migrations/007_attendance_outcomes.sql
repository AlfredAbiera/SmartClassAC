-- SmartClass AC / MariaDB
-- Version: 007
-- Attendance outcomes derived from punches vs schedule (OnTime, Late, Absent, MissingTimeOut).

ALTER TABLE attendance_logs
    ADD COLUMN IF NOT EXISTS outcome VARCHAR(24) NULL AFTER status;

-- Existing closed sessions without punches-vs-schedule classification default to OnTime.
UPDATE attendance_logs
SET outcome = 'OnTime'
WHERE outcome IS NULL AND status = 'Completed';

UPDATE attendance_logs
SET outcome = 'OnTime'
WHERE outcome IS NULL AND status = 'Active';

SET @ck_outcome := (
    SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
    WHERE CONSTRAINT_SCHEMA = DATABASE()
      AND TABLE_NAME = 'attendance_logs'
      AND CONSTRAINT_NAME = 'ck_attendance_logs_outcome'
);
SET @sql_ck_outcome := IF(@ck_outcome = 0,
    'ALTER TABLE attendance_logs ADD CONSTRAINT ck_attendance_logs_outcome CHECK (outcome IS NULL OR outcome IN (''OnTime'', ''Late'', ''Absent'', ''MissingTimeOut''))',
    'SELECT 1');
PREPARE stmt_ck_outcome FROM @sql_ck_outcome;
EXECUTE stmt_ck_outcome;
DEALLOCATE PREPARE stmt_ck_outcome;

SET @ix_outcome := (
    SELECT COUNT(*) FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'attendance_logs'
      AND INDEX_NAME = 'ix_attendance_logs_outcome'
);
SET @sql_ix_outcome := IF(@ix_outcome = 0,
    'CREATE INDEX ix_attendance_logs_outcome ON attendance_logs (outcome, status)',
    'SELECT 1');
PREPARE stmt_ix_outcome FROM @sql_ix_outcome;
EXECUTE stmt_ix_outcome;
DEALLOCATE PREPARE stmt_ix_outcome;
