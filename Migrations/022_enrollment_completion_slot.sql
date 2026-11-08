-- Earlier receipts cannot establish which slot a scanner confirmed.
ALTER TABLE fingerprint_enrollment_completions
    ADD COLUMN IF NOT EXISTS template_identifier BIGINT NULL;
