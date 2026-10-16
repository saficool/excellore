-- Profile database. P = global platform, T = tenant, H = hybrid.
CREATE TABLE user_profiles ( -- [P]
  user_id uuid PRIMARY KEY,
  display_name text CHECK (char_length(display_name) <= 120),
  time_zone text NOT NULL DEFAULT 'UTC' CHECK (char_length(time_zone) <= 80),
  avatar_object_key text,
  mirror_first_name text, mirror_last_name text, mirror_locale text,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(), deleted_at timestamptz
);
CREATE TABLE user_preferences ( -- [P]
  user_id uuid NOT NULL REFERENCES user_profiles(user_id),
  key text NOT NULL CHECK (key IN ('language','timeZone','dateFormat','notifications','theme')),
  value jsonb NOT NULL,
  PRIMARY KEY (user_id,key)
);
CREATE TABLE tenant_member_profiles ( -- [T]
  tenant_id uuid NOT NULL, user_id uuid NOT NULL,
  job_title text CHECK (char_length(job_title) <= 120),
  department text CHECK (char_length(department) <= 120),
  work_phone text CHECK (char_length(work_phone) <= 40),
  directory_visibility text NOT NULL DEFAULT 'visible' CHECK (directory_visibility IN ('visible','hidden')),
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active','suspended')),
  created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id,user_id)
);
CREATE INDEX tenant_member_profiles_directory ON tenant_member_profiles(tenant_id,user_id)
  WHERE status = 'active' AND directory_visibility = 'visible';
ALTER TABLE tenant_member_profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_member_profiles FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_member_profiles_isolation ON tenant_member_profiles
  USING (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid);

CREATE TABLE avatar_uploads ( -- [P]
  id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES user_profiles(user_id),
  object_key text NOT NULL, status text NOT NULL CHECK (status IN ('pending','processed','rejected')),
  content_type text NOT NULL, size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 5242880),
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE data_export_jobs ( -- [P]
  id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES user_profiles(user_id),
  status text NOT NULL CHECK (status IN ('pending','processing','ready','failed')),
  object_key text, requested_at timestamptz NOT NULL DEFAULT now(),
  completed_at timestamptz, expires_at timestamptz
);
CREATE TABLE profile_audit_events ( -- [H]
  id uuid PRIMARY KEY, tenant_id uuid, actor_sub uuid NOT NULL, target_sub uuid NOT NULL,
  action text NOT NULL, before jsonb, after jsonb, reason text,
  correlation_id uuid, occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX profile_audit_events_tenant_time ON profile_audit_events(tenant_id,occurred_at DESC);
ALTER TABLE profile_audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE profile_audit_events FORCE ROW LEVEL SECURITY;
CREATE POLICY profile_audit_events_insert ON profile_audit_events FOR INSERT
  WITH CHECK (actor_sub = nullif(current_setting('app.actor_sub',true),'')::uuid
    AND ((tenant_id IS NULL AND nullif(current_setting('app.tenant_id',true),'') IS NULL)
      OR tenant_id = nullif(current_setting('app.tenant_id',true),'')::uuid));
-- No runtime SELECT, UPDATE or DELETE policy: audit is append-only.

CREATE TABLE outbox ( -- [P], global events only
  id uuid PRIMARY KEY, type text NOT NULL, payload jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(), published_at timestamptz
);
CREATE TABLE inbox ( -- [P]
  event_id uuid PRIMARY KEY, received_at timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE user_profiles IS 'Class P: global profile';
COMMENT ON TABLE user_preferences IS 'Class P: global user preferences';
COMMENT ON TABLE tenant_member_profiles IS 'Class T: tenant member profile, forced RLS';
COMMENT ON TABLE avatar_uploads IS 'Class P: global avatar processing metadata';
COMMENT ON TABLE data_export_jobs IS 'Class P: global data export metadata';
COMMENT ON TABLE profile_audit_events IS 'Class H: append-only global and tenant audit, forced RLS';
COMMENT ON TABLE outbox IS 'Class P: global event publication';
COMMENT ON TABLE inbox IS 'Class P: idempotent event receipt';

ALTER DEFAULT PRIVILEGES FOR ROLE profile_migrator IN SCHEMA public
  REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM profile_app;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM profile_app;
GRANT SELECT, INSERT, UPDATE ON user_profiles, user_preferences, tenant_member_profiles,
  avatar_uploads, data_export_jobs TO profile_app;
GRANT INSERT ON profile_audit_events, outbox, inbox TO profile_app;
GRANT SELECT ON inbox TO profile_app;
GRANT SELECT, UPDATE ON outbox TO profile_publisher;
