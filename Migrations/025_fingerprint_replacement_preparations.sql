-- Replacement preparations are not authorized for sensor writes.
ALTER TABLE fingerprint_enrollment_preparations
    ADD COLUMN replacement_template_id BIGINT NULL;
