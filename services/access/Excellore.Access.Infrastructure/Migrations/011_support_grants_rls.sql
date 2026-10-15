-- [T] Reclassify support grant metadata as tenant-owned, by the owner's security decision.
-- The runtime role can mutate rows only in its transaction-local tenant context.
ALTER TABLE support_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE support_grants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON support_grants
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE UNIQUE INDEX support_grants_one_open_staff_tenant_uq
  ON support_grants (tenant_id, staff_user_id)
  WHERE status IN ('requested','approved','active');
COMMENT ON TABLE support_grants IS 'Class T: tenant-scoped platform support grant workflow';
GRANT INSERT, UPDATE ON support_grants TO access_app;
