-- [T] Restricted Profile audit archive; no application runtime SELECT.
CREATE TABLE profile_audit_archive (LIKE profile_audit_events INCLUDING ALL);
ALTER TABLE profile_audit_archive ADD CONSTRAINT archive_tenant_required
  CHECK (tenant_id IS NOT NULL);
ALTER TABLE profile_audit_archive ENABLE ROW LEVEL SECURITY;
ALTER TABLE profile_audit_archive FORCE ROW LEVEL SECURITY;
CREATE POLICY archive_worker_insert ON profile_audit_archive FOR INSERT TO profile_worker
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid);
CREATE POLICY archive_migrator_read ON profile_audit_archive FOR SELECT TO profile_migrator
  USING (true);
COMMENT ON TABLE profile_audit_archive IS
  'Class T: restricted tenant Profile audit archive, forced RLS';
GRANT INSERT ON profile_audit_archive TO profile_worker;
CREATE FUNCTION profile_archive_append_only_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'Audit archive is append-only' USING ERRCODE='55000';
END $$;
CREATE TRIGGER profile_audit_archive_append_only
  BEFORE UPDATE OR DELETE OR TRUNCATE ON profile_audit_archive
  FOR EACH STATEMENT EXECUTE FUNCTION profile_archive_append_only_guard();
GRANT SELECT, DELETE ON profile_audit_events TO profile_worker;
CREATE POLICY profile_audit_worker_read ON profile_audit_events FOR SELECT TO profile_worker
  USING (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid);
CREATE POLICY profile_audit_worker_erasure ON profile_audit_events FOR DELETE TO profile_worker
  USING (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid
    AND current_setting('app.erasure',true) = 'true');
