-- Text-search parser built on the default parser's built-in C support functions.
CREATE TEXT SEARCH PARSER afd.myparser (START = prsd_start, GETTOKEN = prsd_nexttoken, END = prsd_end, LEXTYPES = prsd_lextype);
