ALTER TABLE class_schedules
    ADD COLUMN IF NOT EXISTS replaces_schedule_id BIGINT NULL AFTER schedule_kind;

CREATE UNIQUE INDEX IF NOT EXISTS uq_class_schedules_replaced_schedule
    ON class_schedules (replaces_schedule_id);

SET @fk_replaced_schedule := (
    SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
    WHERE CONSTRAINT_SCHEMA = DATABASE()
      AND TABLE_NAME = 'class_schedules'
      AND CONSTRAINT_NAME = 'fk_class_schedules_replaced_schedule'
);
SET @sql_replaced_schedule_fk := IF(@fk_replaced_schedule = 0,
    'ALTER TABLE class_schedules ADD CONSTRAINT fk_class_schedules_replaced_schedule FOREIGN KEY (replaces_schedule_id) REFERENCES class_schedules(id) ON DELETE CASCADE',
    'SELECT 1');
PREPARE stmt_replaced_schedule_fk FROM @sql_replaced_schedule_fk;
EXECUTE stmt_replaced_schedule_fk;
DEALLOCATE PREPARE stmt_replaced_schedule_fk;
