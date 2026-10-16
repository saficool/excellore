-- [T] Restricted archive: original audit rows and hash chain survive live-row erasure.
-- No application runtime role can SELECT this table.
CREATE TABLE audit_events_archive (LIKE audit_events INCLUDING ALL);
ALTER TABLE audit_events_archive ADD CONSTRAINT archive_tenant_required
  CHECK (tenant_id IS NOT NULL);
ALTER TABLE audit_events_archive ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events_archive FORCE ROW LEVEL SECURITY;
CREATE POLICY archive_worker_insert ON audit_events_archive FOR INSERT TO access_worker
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid);
CREATE POLICY archive_migrator_read ON audit_events_archive FOR SELECT TO access_migrator
  USING (true);
COMMENT ON TABLE audit_events_archive IS
  'Class T: restricted append-only tenant audit archive, forced RLS';
GRANT INSERT ON audit_events_archive TO access_worker;

CREATE POLICY audit_worker_erasure ON audit_events FOR DELETE TO access_worker
  USING (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid
    AND current_setting('app.erasure',true) = 'true');
CREATE OR REPLACE FUNCTION access_audit_append_only_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE target_tenant uuid;
BEGIN
  target_tenant := nullif(current_setting('app.tenant_id',true),'')::uuid;
  IF TG_OP = 'DELETE' AND current_user = 'access_worker'
     AND current_setting('app.erasure',true) = 'true'
     AND target_tenant IS NOT NULL
     AND EXISTS (SELECT 1 FROM tenant_tombstones
       WHERE tenant_id=target_tenant AND profile_confirmed_at IS NOT NULL)
  THEN
    RETURN NULL;
  END IF;
  RAISE EXCEPTION 'Audit events are append-only' USING ERRCODE = '55000';
END $$;
CREATE TRIGGER audit_events_archive_append_only
  BEFORE UPDATE OR DELETE OR TRUNCATE ON audit_events_archive
  FOR EACH STATEMENT EXECUTE FUNCTION access_audit_append_only_guard();
GRANT DELETE ON audit_events TO access_worker;

-- Erasure needs DELETE only on the listed tenant tables and registry row.
GRANT DELETE ON group_role_assignments, group_members, role_assignments,
  ownership_transfers, role_assignment_batches, access_requests,
  invitations, audit_exports, tenant_settings, tenant_apps,
  support_grants, groups, tenant_members, role_permissions, roles,
  user_tenant_index, tenants TO access_worker;
GRANT SELECT ON ownership_transfers, access_requests, invitations,
  tenant_settings, groups, group_members, role_assignment_batches,
  audit_exports, role_assignments, group_role_assignments,
  role_permissions, user_tenant_index TO access_worker;
GRANT UPDATE (authz_version,updated_at) ON tenants TO access_worker;

CREATE OR REPLACE FUNCTION access_tenant_delete_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF current_user <> 'access_worker' OR
     current_setting('app.erasure',true) <> 'true' OR
     OLD.id IS DISTINCT FROM nullif(current_setting('app.tenant_id',true),'')::uuid OR
     OLD.status <> 'pending_deletion' OR OLD.deletion_started_at IS NULL OR
     NOT EXISTS (SELECT 1 FROM tenant_tombstones WHERE tenant_id=OLD.id
       AND profile_confirmed_at IS NOT NULL AND access_confirmed_at IS NOT NULL)
  THEN
    RAISE EXCEPTION 'Tenant erasure not authorized' USING ERRCODE='55000';
  END IF;
  RETURN OLD;
END $$;
CREATE TRIGGER tenants_erasure_guard BEFORE DELETE ON tenants
  FOR EACH ROW EXECUTE FUNCTION access_tenant_delete_guard();
