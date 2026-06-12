-- Table $fileinputname$. Replace the placeholder columns with your own.
CREATE TABLE $fileinputname$ (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at  timestamptz NOT NULL DEFAULT now()
);
