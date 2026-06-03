-- A concrete partition of afd.events for the 2024 calendar year.
CREATE TABLE afd.events_2024 PARTITION OF afd.events
    FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
