-- [T] Local erasure marker prevents late membership events from recreating data.
CREATE TABLE profile_tenant_tombstones (
  tenant_id uuid PRIMARY KEY,
  erased_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE profile_tenant_tombstones ENABLE ROW LEVEL SECURITY;
ALTER TABLE profile_tenant_tombstones FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON profile_tenant_tombstones
  USING (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid);
COMMENT ON TABLE profile_tenant_tombstones IS
  'Class T: tenant erasure marker under forced RLS';
GRANT SELECT, INSERT ON profile_tenant_tombstones TO profile_worker;

-- Profile now publishes tenant-scoped erasure confirmations as well as global events.
ALTER TABLE outbox ADD COLUMN tenant_id uuid;
GRANT INSERT ON outbox TO profile_worker;
