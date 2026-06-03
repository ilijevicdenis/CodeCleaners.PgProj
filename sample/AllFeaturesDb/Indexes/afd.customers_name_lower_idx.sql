-- Expression index with an explicit btree opclass and ordering options.
CREATE INDEX customers_name_lower_idx
    ON afd.customers USING btree (lower(full_name) text_pattern_ops ASC NULLS LAST);
