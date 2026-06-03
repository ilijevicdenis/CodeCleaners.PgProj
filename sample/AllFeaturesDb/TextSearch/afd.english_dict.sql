-- Text-search dictionary built from the built-in simple template.
CREATE TEXT SEARCH DICTIONARY afd.english_dict (
    TEMPLATE  = pg_catalog.simple,
    STOPWORDS = english
);
