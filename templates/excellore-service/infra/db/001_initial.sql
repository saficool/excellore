-- Apply with __APP_SLUG___migrator after provisioning the app and publisher roles.
-- Class T: sample_items (tenant-owned). Class P: inbox and outbox (service infrastructure).
CREATE TABLE sample_items (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    code varchar(64) NOT NULL
);
COMMENT ON TABLE sample_items IS 'Class T: tenant-owned sample items';
CREATE INDEX sample_items_tenant_id_idx ON sample_items (tenant_id, id);
ALTER TABLE sample_items ENABLE ROW LEVEL SECURITY;
CREATE POLICY sample_items_tenant_isolation ON sample_items
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
ALTER TABLE sample_items FORCE ROW LEVEL SECURITY;

CREATE TABLE inbox (
    event_id uuid PRIMARY KEY,
    processed_at timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE inbox IS 'Class P: idempotent event receipt';

CREATE TABLE outbox (
    id uuid PRIMARY KEY,
    type varchar(128) NOT NULL,
    tenant_id uuid,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz
);
COMMENT ON TABLE outbox IS 'Class P: transactional event publication; tenant_id is a routing key';
CREATE INDEX outbox_pending_idx ON outbox (created_at, id) WHERE published_at IS NULL;

GRANT USAGE ON SCHEMA public TO "__APP_SLUG___app", "__APP_SLUG___publisher";
GRANT SELECT, INSERT, UPDATE, DELETE ON sample_items TO "__APP_SLUG___app";
GRANT SELECT, INSERT ON inbox TO "__APP_SLUG___app";
GRANT INSERT ON outbox TO "__APP_SLUG___app";
GRANT SELECT ON outbox TO "__APP_SLUG___publisher";
GRANT UPDATE (published_at) ON outbox TO "__APP_SLUG___publisher";
