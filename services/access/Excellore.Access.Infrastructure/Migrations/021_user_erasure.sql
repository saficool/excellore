-- [P] Application erasure receipt. The Keycloak subject remains only as an opaque
-- tombstone key so an active external identity cannot recreate Access data.
CREATE TABLE user_erasure_requests (
  request_id uuid PRIMARY KEY,
  user_id uuid NOT NULL UNIQUE,
  tombstone_id uuid NOT NULL UNIQUE,
  status text NOT NULL DEFAULT 'processing'
    CHECK (status IN ('processing','pending_external_identity_deletion')),
  profile_confirmed_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE user_erasure_requests IS
  'Class P: opaque application-erasure tombstone; external Keycloak deletion remains pending';

GRANT SELECT, INSERT, UPDATE (status,profile_confirmed_at,updated_at)
  ON user_erasure_requests TO access_worker;
GRANT SELECT (user_id) ON user_erasure_requests TO access_app;
GRANT SELECT, UPDATE (email,email_verified,given_name,family_name,status,updated_at)
  ON identities TO access_worker;

-- Tenant erasure already grants DELETE on the affected T tables. These narrowly
-- scoped column grants anonymize retained live records under forced RLS.
GRANT UPDATE (created_by) ON roles TO access_worker;
GRANT UPDATE (granted_by) ON role_assignments,group_role_assignments TO access_worker;
GRANT UPDATE (invited_by) ON tenant_members TO access_worker;
GRANT UPDATE (invited_by,accepted_by) ON invitations TO access_worker;
GRANT UPDATE (requester_id,decided_by) ON access_requests TO access_worker;
GRANT UPDATE (approved_by) ON support_grants TO access_worker;
GRANT UPDATE (requested_by) ON audit_exports,tenant_exports,role_assignment_batches TO access_worker;

