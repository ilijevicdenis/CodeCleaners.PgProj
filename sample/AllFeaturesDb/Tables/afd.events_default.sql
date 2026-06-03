-- DEFAULT partition catches rows outside every explicit range.
CREATE TABLE afd.events_default PARTITION OF afd.events DEFAULT;
