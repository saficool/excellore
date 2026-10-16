-- [T] Opaque completion marker; intentionally no FK to the live tenant registry.
CREATE TABLE tenant_tombstones (
  tenant_id uuid PRIMARY KEY,
  profile_confirmed_at timestamptz,
  access_confirmed_at timestamptz,
  completed_at timestamptz
);
ALTER TABLE tenant_tombstones ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_tombstones FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant_tombstones
  USING (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid);
COMMENT ON TABLE tenant_tombstones IS
  'Class T: opaque tenant deletion tombstone and erasure receipts, forced RLS';
GRANT SELECT, INSERT, UPDATE (profile_confirmed_at, access_confirmed_at, completed_at)
  ON tenant_tombstones TO access_worker;
GRANT SELECT, INSERT ON inbox TO access_worker;
