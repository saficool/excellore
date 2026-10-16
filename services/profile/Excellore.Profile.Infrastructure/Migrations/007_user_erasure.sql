-- [P] User erasure request state remains visible while external Keycloak deletion
-- is pending. The subject is retained only as an opaque tombstone key.
CREATE TABLE profile_user_erasure_requests (
  request_id uuid PRIMARY KEY,
  user_id uuid NOT NULL UNIQUE,
  status text NOT NULL DEFAULT 'pending_application_erasure'
    CHECK (status IN ('pending_application_erasure','pending_external_identity_deletion')),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE profile_user_tombstones (
  user_id uuid PRIMARY KEY,
  request_id uuid NOT NULL UNIQUE REFERENCES profile_user_erasure_requests(request_id),
  erased_at timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE profile_user_erasure_requests IS
  'Class P: user erasure state; external Keycloak deletion remains pending';
COMMENT ON TABLE profile_user_tombstones IS
  'Class P: opaque app tombstone preventing profile recreation';

GRANT SELECT,INSERT ON profile_user_erasure_requests TO profile_app;
GRANT SELECT ON profile_user_tombstones TO profile_app;
GRANT SELECT,UPDATE (status,updated_at) ON profile_user_erasure_requests TO profile_worker;
GRANT SELECT,INSERT ON profile_user_tombstones TO profile_worker;
GRANT SELECT,DELETE ON avatar_uploads,user_preferences,data_export_jobs TO profile_worker;
GRANT UPDATE (display_name,time_zone,avatar_object_key,mirror_first_name,mirror_last_name,
  mirror_locale,updated_at,deleted_at) ON user_profiles TO profile_worker;
GRANT INSERT ON profile_audit_events TO profile_worker;

-- DELETE was granted in 002_worker_projections.sql. The erasure handler first
-- discovers this user's tenants through the existing user-bounded export policy,
-- then deletes inside each transaction-local tenant context.

