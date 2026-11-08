CREATE TABLE IF NOT EXISTS buzzer_settings (
    singleton_id TINYINT NOT NULL PRIMARY KEY,
    enabled TINYINT(1) NOT NULL,
    lead_minutes INT NOT NULL,
    repeat_minutes INT NOT NULL,
    CONSTRAINT ck_buzzer_singleton CHECK (singleton_id = 1),
    CONSTRAINT ck_buzzer_lead CHECK (lead_minutes BETWEEN 1 AND 180),
    CONSTRAINT ck_buzzer_repeat CHECK (repeat_minutes BETWEEN 1 AND 180)
) ENGINE=InnoDB;

INSERT IGNORE INTO buzzer_settings (singleton_id, enabled, lead_minutes, repeat_minutes)
VALUES (1, TRUE, 15, 5);
