-- NOTE: btree_gist ships in the standard postgres:18 contrib set and is required
-- by the EXCLUDE constraint in afd.room_booking (lets a scalar column participate
-- in a GiST exclusion alongside a range).
CREATE EXTENSION IF NOT EXISTS btree_gist;
