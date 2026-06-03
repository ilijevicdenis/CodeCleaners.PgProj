-- Custom binary operator with commutator and a selectivity estimator.
CREATE OPERATOR afd.=== (
    FUNCTION   = afd.mood_eq,
    LEFTARG    = afd.mood,
    RIGHTARG   = afd.mood,
    COMMUTATOR = OPERATOR(afd.===),
    HASHES,
    MERGES
);
