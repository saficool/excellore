-- [T] WP-10 role assignment jobs. Target IDs remain in the database, never URLs/events/logs.
CREATE TABLE role_assignment_batches (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  requested_by uuid NOT NULL,
  role_id uuid NOT NULL,
  target_user_ids uuid[] NOT NULL,
  expires_at timestamptz,
  reason text NOT NULL,
  status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','running','completed')),
  cursor integer NOT NULL DEFAULT 0,
  succeeded integer NOT NULL DEFAULT 0,
  failed integer NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT now(),
  completed_at timestamptz,
  CHECK (cardinality(target_user_ids) BETWEEN 1 AND 100),
  CHECK (cursor >= 0 AND cursor <= cardinality(target_user_ids)),
  CHECK (succeeded >= 0 AND failed >= 0 AND succeeded + failed = cursor)
);
CREATE UNIQUE INDEX role_assignment_batches_one_pending
  ON role_assignment_batches(tenant_id) WHERE status IN ('pending','running');
CREATE INDEX role_assignment_batches_tenant_created
  ON role_assignment_batches(tenant_id, created_at DESC);
ALTER TABLE role_assignment_batches ENABLE ROW LEVEL SECURITY;
ALTER TABLE role_assignment_batches FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON role_assignment_batches
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
COMMENT ON TABLE role_assignment_batches IS 'Class T: tenant bulk role assignment jobs';
GRANT SELECT, INSERT ON role_assignment_batches TO access_app;
GRANT SELECT, UPDATE (status, cursor, succeeded, failed, completed_at)
  ON role_assignment_batches TO access_worker;
GRANT SELECT ON roles, role_permissions, permissions, apps, tenant_apps, tenant_members
  TO access_worker;
GRANT INSERT ON role_assignments TO access_worker;
