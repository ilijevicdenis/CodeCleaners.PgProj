-- Cast from the enum to integer using a SQL function, available on assignment.
CREATE CAST (afd.mood AS integer) WITH FUNCTION afd.mood_to_int(afd.mood) AS ASSIGNMENT;
