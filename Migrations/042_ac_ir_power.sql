ALTER TABLE ac_ir_state
    ADD COLUMN IF NOT EXISTS assumed_power BOOLEAN NULL,
    ADD COLUMN IF NOT EXISTS command_power BOOLEAN NULL,
    ADD COLUMN IF NOT EXISTS protocol_version INT NOT NULL DEFAULT 1;

ALTER TABLE ac_ir_reconciliations
    ADD COLUMN IF NOT EXISTS assumed_power BOOLEAN NULL;

ALTER TABLE classrooms
    ADD COLUMN IF NOT EXISTS ir_attendance_power BOOLEAN NOT NULL DEFAULT FALSE;

UPDATE classrooms SET ir_attendance_power = TRUE WHERE ac_status = 'Cooling';
