CREATE TABLE IF NOT EXISTS gsm_modem_settings (
    singleton_id TINYINT NOT NULL PRIMARY KEY,
    port_name VARCHAR(32) NOT NULL,
    baud_rate INT NOT NULL,
    updated_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT ck_gsm_modem_singleton CHECK (singleton_id = 1)
) ENGINE=InnoDB;
