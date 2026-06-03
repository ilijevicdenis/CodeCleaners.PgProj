-- Table OF a composite type: columns come from afd.address.
CREATE TABLE afd.address_row OF afd.address (
    PRIMARY KEY (zip)
);
