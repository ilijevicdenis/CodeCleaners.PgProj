-- Text-search template built on the simple dictionary's built-in C support functions.
CREATE TEXT SEARCH TEMPLATE afd.mytmpl (INIT = dsimple_init, LEXIZE = dsimple_lexize);
