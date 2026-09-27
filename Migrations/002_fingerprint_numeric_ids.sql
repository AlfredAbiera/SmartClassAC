-- SmartClass AC / MariaDB
-- Version: 002
-- Switch fingerprint template IDs from UUID strings to numeric device IDs.

DELETE FROM fingerprint_templates;

ALTER TABLE fingerprint_templates
    MODIFY COLUMN template_identifier BIGINT NOT NULL;
