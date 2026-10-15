-- [T] Audit export jobs. WP-10 worker will produce the private object and mark it ready.
CREATE TABLE audit_exports (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  requested_by uuid NOT NULL,
  status text NOT NULL CHECK (status IN ('pending','ready','failed')),
  object_key text,
  created_at timestamptz NOT NULL DEFAULT now(),
  ready_at timestamptz,
  CHECK ((status = 'ready') = (object_key IS NOT NULL))
);
CREATE INDEX audit_exports_tenant_time ON audit_exports (tenant_id, created_at DESC);
ALTER TABLE audit_exports ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_exports FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON audit_exports
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
COMMENT ON TABLE audit_exports IS 'Class T: private tenant audit export jobs';
GRANT SELECT, INSERT, UPDATE ON audit_exports TO access_app;
