-- Sample schema + data for the "test-data" DatabaseConnections entry in
-- appsettings.Development.json (databaseQuery workflow node, phase 3). Not touched by EF
-- migrations — this is a separate database, entirely unrelated to AgentStudio's own schema,
-- meant only as something to point a databaseQuery node at while building/testing a workflow.
--
-- Load into a fresh "agentstudio_test" database on the same Postgres instance used for dev:
--   docker exec agentstudio-postgres psql -U agentstudio -d postgres -c "CREATE DATABASE agentstudio_test OWNER agentstudio;"
--   docker exec -i agentstudio-postgres psql -U agentstudio -d agentstudio_test < test-data.sql

CREATE TABLE customers (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    email TEXT NOT NULL,
    country TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE orders (
    id SERIAL PRIMARY KEY,
    customer_id INTEGER NOT NULL REFERENCES customers(id),
    product TEXT NOT NULL,
    amount NUMERIC(10,2) NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO customers (name, email, country) VALUES
    ('Anna Kowalska', 'anna.kowalska@example.com', 'PL'),
    ('John Smith', 'john.smith@example.com', 'GB'),
    ('Marie Dubois', 'marie.dubois@example.com', 'FR'),
    ('Lars Nilsson', 'lars.nilsson@example.com', 'SE'),
    ('Piotr Nowak', 'piotr.nowak@example.com', 'PL');

INSERT INTO orders (customer_id, product, amount, status) VALUES
    (1, 'Widget A', 49.99, 'completed'),
    (1, 'Widget B', 19.99, 'completed'),
    (2, 'Gadget Pro', 129.00, 'pending'),
    (2, 'Widget A', 49.99, 'cancelled'),
    (3, 'Gizmo Mini', 9.99, 'completed'),
    (3, 'Gadget Pro', 129.00, 'completed'),
    (4, 'Widget B', 19.99, 'pending'),
    (5, 'Gizmo Mini', 9.99, 'completed'),
    (5, 'Gadget Pro', 129.00, 'completed'),
    (5, 'Widget A', 49.99, 'pending');
