ALTER TABLE classrooms
    ADD COLUMN minimum_temperature INT NOT NULL DEFAULT 16,
    ADD CONSTRAINT ck_classrooms_minimum_temperature CHECK (minimum_temperature BETWEEN 16 AND 30);
