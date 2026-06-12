-- Seed for the pgproj sample database (PG18). Deliberately broad rather than deep: it touches the
-- object kinds the tooling round-trips (schemas, tables with identity/PK/FK/check/defaults, enum +
-- domain types, sequences, indexes incl. partial & expression, views, a materialized view,
-- SQL + PL/pgSQL functions, a trigger, comments, and an RLS policy) so `pgproj extract` of this
-- database produces a project that genuinely exercises the parser, model, and editors.

CREATE SCHEMA sales;
CREATE SCHEMA inventory;
CREATE SCHEMA audit;

-- ---- types -------------------------------------------------------------------------------

CREATE TYPE sales.order_status AS ENUM ('draft', 'placed', 'paid', 'shipped', 'cancelled');

CREATE DOMAIN sales.email AS text
  CHECK (VALUE ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$');

-- ---- inventory ---------------------------------------------------------------------------

CREATE TABLE inventory.products (
    id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku         text NOT NULL UNIQUE,
    name        text NOT NULL,
    unit_price  numeric(12,2) NOT NULL CHECK (unit_price >= 0),
    discontinued boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_products_active_name ON inventory.products (name) WHERE NOT discontinued;

CREATE TABLE inventory.stock_levels (
    product_id  integer NOT NULL REFERENCES inventory.products(id),
    warehouse   text NOT NULL,
    quantity    integer NOT NULL DEFAULT 0 CHECK (quantity >= 0),
    PRIMARY KEY (product_id, warehouse)
);

-- ---- sales -------------------------------------------------------------------------------

CREATE SEQUENCE sales.order_number_seq START WITH 10000;

CREATE TABLE sales.customers (
    id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        text NOT NULL,
    email       sales.email,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_customers_email_lower ON sales.customers (lower(email));

CREATE TABLE sales.orders (
    id           integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number bigint NOT NULL DEFAULT nextval('sales.order_number_seq'),
    customer_id  integer NOT NULL REFERENCES sales.customers(id),
    status       sales.order_status NOT NULL DEFAULT 'draft',
    placed_at    timestamptz,
    CONSTRAINT placed_orders_have_timestamp CHECK (status = 'draft' OR placed_at IS NOT NULL)
);

CREATE TABLE sales.order_lines (
    order_id    integer NOT NULL REFERENCES sales.orders(id),
    line_no     integer NOT NULL,
    product_id  integer NOT NULL REFERENCES inventory.products(id),
    quantity    integer NOT NULL CHECK (quantity > 0),
    unit_price  numeric(12,2) NOT NULL,
    PRIMARY KEY (order_id, line_no)
);

-- ---- audit -------------------------------------------------------------------------------

CREATE TABLE audit.order_status_changes (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id    integer NOT NULL,
    old_status  sales.order_status,
    new_status  sales.order_status NOT NULL,
    changed_at  timestamptz NOT NULL DEFAULT now(),
    changed_by  text NOT NULL DEFAULT current_user
);

-- ---- functions / trigger ------------------------------------------------------------------

CREATE FUNCTION sales.order_total(p_order_id integer) RETURNS numeric
LANGUAGE sql STABLE AS $$
    SELECT COALESCE(sum(quantity * unit_price), 0)
    FROM sales.order_lines
    WHERE order_id = p_order_id;
$$;

CREATE FUNCTION audit.log_order_status_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM OLD.status THEN
        INSERT INTO audit.order_status_changes (order_id, old_status, new_status)
        VALUES (NEW.id, OLD.status, NEW.status);
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_orders_status_audit
AFTER UPDATE ON sales.orders
FOR EACH ROW EXECUTE FUNCTION audit.log_order_status_change();

-- ---- views --------------------------------------------------------------------------------

CREATE VIEW sales.v_open_orders AS
SELECT o.id, o.order_number, c.name AS customer_name, o.status, sales.order_total(o.id) AS total
FROM sales.orders o
JOIN sales.customers c ON c.id = o.customer_id
WHERE o.status IN ('placed', 'paid');

CREATE MATERIALIZED VIEW sales.mv_revenue_by_customer AS
SELECT c.id AS customer_id, c.name, sum(l.quantity * l.unit_price) AS revenue
FROM sales.customers c
JOIN sales.orders o ON o.customer_id = c.id AND o.status <> 'cancelled'
JOIN sales.order_lines l ON l.order_id = o.id
GROUP BY c.id, c.name;

-- ---- row-level security ---------------------------------------------------------------------

ALTER TABLE audit.order_status_changes ENABLE ROW LEVEL SECURITY;

CREATE POLICY order_audit_read_own ON audit.order_status_changes
    FOR SELECT USING (changed_by = current_user);

-- ---- comments -------------------------------------------------------------------------------

COMMENT ON SCHEMA sales IS 'Customer-facing ordering.';
COMMENT ON TABLE sales.orders IS 'One row per order; status drives the audit trigger.';
COMMENT ON FUNCTION sales.order_total(integer) IS 'Sum of line totals for one order.';

-- ---- a little data so extract/compare/drift have something real to chew on ------------------

INSERT INTO inventory.products (sku, name, unit_price) VALUES
    ('SKU-001', 'Industrial degreaser 5L', 24.90),
    ('SKU-002', 'Microfiber cloth pack',    9.50),
    ('SKU-003', 'Floor polisher rental',  120.00);

INSERT INTO sales.customers (name, email) VALUES
    ('Contoso d.o.o.',  'purchasing@contoso.example'),
    ('Fabrikam GmbH',   'office@fabrikam.example');

INSERT INTO sales.orders (customer_id, status, placed_at) VALUES
    (1, 'placed', now() - interval '2 days'),
    (2, 'paid',   now() - interval '1 day');

INSERT INTO sales.order_lines (order_id, line_no, product_id, quantity, unit_price) VALUES
    (1, 1, 1, 4, 24.90),
    (1, 2, 2, 10, 9.50),
    (2, 1, 3, 1, 120.00);

INSERT INTO inventory.stock_levels (product_id, warehouse, quantity) VALUES
    (1, 'ZG-01', 40), (2, 'ZG-01', 250), (3, 'ZG-02', 3);

REFRESH MATERIALIZED VIEW sales.mv_revenue_by_customer;
