CREATE TABLE IF NOT EXISTS employee_number_sequence (
    singleton_id TINYINT NOT NULL PRIMARY KEY,
    last_value BIGINT NOT NULL DEFAULT 0,
    CONSTRAINT ck_employee_number_sequence_singleton CHECK (singleton_id = 1)
) ENGINE=InnoDB;

INSERT INTO employee_number_sequence (singleton_id, last_value)
SELECT 1, COALESCE(MAX(CAST(SUBSTRING(employee_number, 5) AS UNSIGNED)), 0)
FROM user_accounts
WHERE employee_number REGEXP '^EMP-[0-9]{1,18}$'
ON DUPLICATE KEY UPDATE last_value = GREATEST(last_value, VALUES(last_value));
