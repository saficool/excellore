-- [T] Ownership nomination and acceptance. An unaccepted nomination grants no authority.
CREATE TABLE ownership_transfers (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  from_user_id uuid NOT NULL,
  to_user_id uuid NOT NULL,
  reason text NOT NULL,
  status text NOT NULL CHECK (status IN ('pending','accepted','expired')),
  expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  accepted_at timestamptz,
  FOREIGN KEY (tenant_id, from_user_id) REFERENCES tenant_members(tenant_id, user_id),
  FOREIGN KEY (tenant_id, to_user_id) REFERENCES tenant_members(tenant_id, user_id),
  CHECK (from_user_id <> to_user_id)
);
CREATE UNIQUE INDEX ownership_transfers_one_pending_uq
  ON ownership_transfers (tenant_id, from_user_id) WHERE status = 'pending';
ALTER TABLE ownership_transfers ENABLE ROW LEVEL SECURITY;
ALTER TABLE ownership_transfers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON ownership_transfers
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
COMMENT ON TABLE ownership_transfers IS 'Class T: pending ownership nominations';
GRANT SELECT, INSERT, UPDATE ON ownership_transfers TO access_app;
