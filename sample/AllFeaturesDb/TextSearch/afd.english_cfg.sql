-- Text-search configuration copied from the built-in english config, then remapped
-- to use our dictionary for word tokens.
CREATE TEXT SEARCH CONFIGURATION afd.english_cfg (COPY = pg_catalog.english);

ALTER TEXT SEARCH CONFIGURATION afd.english_cfg
    ALTER MAPPING FOR asciiword, word WITH afd.english_dict;
