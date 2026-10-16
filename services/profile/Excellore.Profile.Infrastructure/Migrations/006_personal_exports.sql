-- [P] Worker processes global personal export jobs; user-specific tenant reads remain under forced RLS.
GRANT SELECT ON user_profiles, user_preferences, data_export_jobs TO profile_worker;
GRANT UPDATE (status, object_key, completed_at, expires_at) ON data_export_jobs TO profile_worker;
CREATE POLICY profile_worker_personal_export ON tenant_member_profiles FOR SELECT TO profile_worker
  USING (user_id = nullif(current_setting('app.export_user_id',true),'')::uuid
    AND current_setting('app.export_mode',true) = 'personal');
