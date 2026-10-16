-- [T] One private tenant export request per UTC calendar day.
CREATE TABLE tenant_exports (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  requested_by uuid NOT NULL,
  requested_day date NOT NULL DEFAULT (now() AT TIME ZONE 'UTC')::date,
  status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','ready','failed')),
  object_key text,
  created_at timestamptz NOT NULL DEFAULT now(),
  ready_at timestamptz,
  CONSTRAINT tenant_exports_ready_key CHECK ((status = 'ready') = (object_key IS NOT NULL)),
  CONSTRAINT tenant_exports_daily_limit UNIQUE (tenant_id, requested_day)
);
ALTER TABLE tenant_exports ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_exports FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant_exports
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
COMMENT ON TABLE tenant_exports IS 'Class T: private tenant export jobs, one per UTC day';
GRANT SELECT, INSERT (id, tenant_id, requested_by) ON tenant_exports TO access_app;
GRANT SELECT, DELETE ON tenant_exports TO access_worker;
