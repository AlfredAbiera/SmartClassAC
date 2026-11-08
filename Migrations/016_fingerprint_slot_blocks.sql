-- Blocks are global because legacy fingerprint slot mappings are global.
CREATE TABLE IF NOT EXISTS fingerprint_slot_blocks (
    template_identifier BIGINT NOT NULL PRIMARY KEY,
    blocked_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT ck_fingerprint_slot_block_positive CHECK (template_identifier > 0)
) ENGINE=InnoDB;
