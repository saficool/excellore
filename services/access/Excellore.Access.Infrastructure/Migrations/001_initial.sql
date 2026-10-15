-- ===== Platform tables [P] =====
CREATE TABLE tenants (                       -- [P]
  id uuid PRIMARY KEY,
  slug text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$'),
  name text NOT NULL,
  status text NOT NULL CHECK (status IN ('provisioning','active','suspended','pending_deletion','deleted')),
  plan text NOT NULL DEFAULT 'starter',
  region text NOT NULL DEFAULT 'default',
  authz_version bigint NOT NULL DEFAULT 1,   -- bumped on any change affecting permissions
  deletion_requested_at timestamptz,
  deletion_due_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz
);

CREATE TABLE apps (                          -- [P]
  id text PRIMARY KEY,                       -- 'profile', 'access', …
  name text NOT NULL,
  core boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE permissions (                   -- [P] synced from permissions.yaml
  key text PRIMARY KEY,                      -- '<app>.<resource>.<action>'
  app_id text NOT NULL REFERENCES apps(id),
  description text NOT NULL,
  privileged boolean NOT NULL DEFAULT false,
  owner_only boolean NOT NULL DEFAULT false,
  deprecated boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE identities (                    -- [P] mirror of Keycloak identities; never listed directly
  user_id uuid PRIMARY KEY,                  -- Keycloak sub
  email text NOT NULL,
  email_verified boolean NOT NULL DEFAULT false,
  given_name text, family_name text,
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active','disabled','deleted')),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  last_seen_at timestamptz
);
CREATE UNIQUE INDEX identities_email_uq ON identities (lower(email));

-- Tenant-keyed platform tables: carry tenant_id but are NOT tenant-isolated by RLS.
-- They MUST be listed in infra/db/rls-exempt.txt (CODEOWNERS: security owner).
CREATE TABLE user_tenant_index (             -- [P, exempt] lookup "which tenants is this user in"
  user_id uuid NOT NULL REFERENCES identities(user_id),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  membership_status text NOT NULL CHECK (membership_status IN ('invited','active','suspended','removed')),
  PRIMARY KEY (user_id, tenant_id)
);

CREATE TABLE support_grants (                -- [P, exempt] platform staff access metadata only
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  staff_user_id uuid NOT NULL REFERENCES identities(user_id),
  role_id uuid NOT NULL,
  reason text NOT NULL,
  status text NOT NULL CHECK (status IN ('requested','approved','active','expired','revoked','rejected')),
  approved_by uuid,
  starts_at timestamptz, expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE outbox (                        -- [P, exempt] publisher reads without tenant context
  id uuid PRIMARY KEY,
  type text NOT NULL,
  tenant_id uuid,                            -- routing metadata only
  payload jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  published_at timestamptz
);
CREATE TABLE inbox (event_id uuid PRIMARY KEY, received_at timestamptz NOT NULL DEFAULT now());  -- [P]

-- ===== Hybrid tables [H] =====
CREATE TABLE roles (                         -- [H] system rows have tenant_id NULL
  id uuid PRIMARY KEY,
  tenant_id uuid REFERENCES tenants(id),
  name text NOT NULL,
  description text,
  is_system boolean NOT NULL DEFAULT false,
  privileged boolean NOT NULL DEFAULT false, -- derived: contains a privileged permission
  created_by uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((is_system AND tenant_id IS NULL) OR (NOT is_system AND tenant_id IS NOT NULL))
);
CREATE UNIQUE INDEX roles_name_uq
  ON roles (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));

CREATE TABLE role_permissions (              -- [H]
  role_id uuid NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
  tenant_id uuid REFERENCES tenants(id),     -- denormalized from roles for RLS
  permission_key text NOT NULL REFERENCES permissions(key),
  PRIMARY KEY (role_id, permission_key)
);

CREATE TABLE audit_events (                  -- [H] append-only; tenant_id NULL = platform scope
  id uuid PRIMARY KEY,
  tenant_id uuid REFERENCES tenants(id),
  occurred_at timestamptz NOT NULL DEFAULT now(),
  actor_sub uuid, actor_type text NOT NULL CHECK (actor_type IN ('human','service','system')),
  action text NOT NULL,
  target_type text, target_id text,
  before jsonb, after jsonb,
  reason text, acr text, source_ip inet,
  support_grant_id uuid,
  correlation_id uuid,
  prev_hash bytea, hash bytea NOT NULL       -- hash chain per tenant_id scope; serialize with an advisory lock
);
CREATE INDEX audit_events_tenant_time ON audit_events (tenant_id, occurred_at DESC);

-- ===== Tenant tables [T] =====
CREATE TABLE tenant_members (                -- [T]
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  user_id uuid NOT NULL REFERENCES identities(user_id),
  status text NOT NULL CHECK (status IN ('invited','active','suspended','removed')),
  is_support boolean NOT NULL DEFAULT false,
  support_grant_id uuid,
  invited_by uuid,
  joined_at timestamptz,
  expires_at timestamptz,                    -- for support memberships
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, user_id)
);

CREATE TABLE role_assignments (              -- [T]
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL,
  user_id uuid NOT NULL,
  role_id uuid NOT NULL REFERENCES roles(id),
  expires_at timestamptz,
  reason text,
  granted_by uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  revoked_at timestamptz,
  FOREIGN KEY (tenant_id, user_id) REFERENCES tenant_members (tenant_id, user_id)
);
CREATE UNIQUE INDEX role_assignments_active_uq
  ON role_assignments (tenant_id, user_id, role_id) WHERE revoked_at IS NULL;

CREATE TABLE groups (                        -- [T]
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES tenants(id),
  name text NOT NULL, description text,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX groups_id_tenant_uq ON groups (id, tenant_id);
CREATE UNIQUE INDEX groups_name_uq ON groups (tenant_id, lower(name));

CREATE TABLE group_members (                 -- [T]
  tenant_id uuid NOT NULL, group_id uuid NOT NULL,
  user_id uuid NOT NULL,
  PRIMARY KEY (tenant_id, group_id, user_id),
  FOREIGN KEY (group_id, tenant_id) REFERENCES groups (id, tenant_id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, user_id) REFERENCES tenant_members (tenant_id, user_id)
);

CREATE TABLE group_role_assignments (        -- [T]
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  group_id uuid NOT NULL,
  role_id uuid NOT NULL REFERENCES roles(id),
  expires_at timestamptz, granted_by uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(), revoked_at timestamptz,
  FOREIGN KEY (group_id, tenant_id) REFERENCES groups (id, tenant_id) ON DELETE CASCADE
);

CREATE TABLE tenant_apps (                   -- [T] first gate: which apps the tenant may use
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  app_id text NOT NULL REFERENCES apps(id),
  status text NOT NULL CHECK (status IN ('enabled','disabled')),
  features jsonb NOT NULL DEFAULT '{}',      -- e.g. {"maxMembers":100,"maxCustomRoles":25}
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, app_id)
);

CREATE TABLE tenant_settings (               -- [T]
  tenant_id uuid PRIMARY KEY REFERENCES tenants(id),
  settings jsonb NOT NULL DEFAULT '{}',
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE invitations (                   -- [T]
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  email text NOT NULL,
  role_ids jsonb NOT NULL DEFAULT '[]',
  group_ids jsonb NOT NULL DEFAULT '[]',
  token_hash bytea NOT NULL UNIQUE,          -- SHA-256 of the secret part; token = <tenantId>.<secret>
  status text NOT NULL CHECK (status IN ('pending','accepted','revoked','expired')),
  invited_by uuid NOT NULL,
  expires_at timestamptz NOT NULL,
  accepted_by uuid, accepted_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE access_requests (               -- [T]
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES tenants(id),
  requester_id uuid NOT NULL,
  target_type text NOT NULL CHECK (target_type IN ('role','app')),
  target_key text NOT NULL, reason text NOT NULL, requested_until timestamptz,
  status text NOT NULL CHECK (status IN ('pending','approved','rejected','cancelled','expired')),
  decided_by uuid, decided_at timestamptz, decision_note text,
  created_at timestamptz NOT NULL DEFAULT now()
);

-- ===== RLS: apply to every [T] and [H] table =====
-- Helper expressions:
--   ctx   : NULLIF(current_setting('app.tenant_id', true), '')::uuid
--   scope : NULLIF(current_setting('app.scope',     true), '')
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['tenant_members','role_assignments','groups','group_members',
                           'group_role_assignments','tenant_apps','tenant_settings',
                           'invitations','access_requests'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON %I
        USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
  END LOOP;
END $$;

-- Hybrid: system rows (tenant_id IS NULL) readable by all; writable only in platform scope.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['roles','role_permissions'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY read_rows ON %I FOR SELECT
        USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    EXECUTE format($p$CREATE POLICY write_rows ON %I FOR ALL
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
               OR (tenant_id IS NULL AND NULLIF(current_setting('app.scope', true), '') = 'platform'))
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
               OR (tenant_id IS NULL AND NULLIF(current_setting('app.scope', true), '') = 'platform'))$p$, t);
  END LOOP;
END $$;

-- audit_events: tenant rows by tenant context; platform rows (tenant_id IS NULL) only in platform scope.
ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events FORCE ROW LEVEL SECURITY;
CREATE POLICY audit_select ON audit_events FOR SELECT
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
         OR (tenant_id IS NULL AND NULLIF(current_setting('app.scope', true), '') = 'platform'));
CREATE POLICY audit_insert ON audit_events FOR INSERT
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
         OR (tenant_id IS NULL AND NULLIF(current_setting('app.scope', true), '') = 'platform'));
-- PostgreSQL foreign keys alone do not prevent a row from referencing another
-- tenant's globally keyed role. Validate the role's tenant in the writing scope.
CREATE FUNCTION access_role_permission_tenant_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE role_tenant uuid;
BEGIN
  SELECT tenant_id INTO role_tenant FROM roles WHERE id = NEW.role_id;
  IF NOT FOUND OR NEW.tenant_id IS DISTINCT FROM role_tenant THEN
    RAISE EXCEPTION 'Role unavailable in tenant context' USING ERRCODE = '23514';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER role_permissions_tenant_guard
  BEFORE INSERT OR UPDATE OF role_id, tenant_id ON role_permissions
  FOR EACH ROW EXECUTE FUNCTION access_role_permission_tenant_guard();

CREATE FUNCTION access_assigned_role_tenant_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE role_tenant uuid;
BEGIN
  SELECT tenant_id INTO role_tenant FROM roles WHERE id = NEW.role_id;
  IF NOT FOUND OR (role_tenant IS NOT NULL AND role_tenant <> NEW.tenant_id) THEN
    RAISE EXCEPTION 'Role unavailable in tenant context' USING ERRCODE = '23514';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER role_assignments_tenant_guard
  BEFORE INSERT OR UPDATE OF role_id, tenant_id ON role_assignments
  FOR EACH ROW EXECUTE FUNCTION access_assigned_role_tenant_guard();
CREATE TRIGGER group_role_assignments_tenant_guard
  BEFORE INSERT OR UPDATE OF role_id, tenant_id ON group_role_assignments
  FOR EACH ROW EXECUTE FUNCTION access_assigned_role_tenant_guard();

-- Persist the handbook's P/H/T classification in PostgreSQL metadata.
COMMENT ON TABLE tenants IS 'Class P: tenant registry';
COMMENT ON TABLE apps IS 'Class P: application registry';
COMMENT ON TABLE permissions IS 'Class P: code-first permission catalog';
COMMENT ON TABLE identities IS 'Class P: Keycloak identity mirror';
COMMENT ON TABLE user_tenant_index IS 'Class P, RLS-exempt: membership lookup index';
COMMENT ON TABLE support_grants IS 'Class P, RLS-exempt: staff support metadata';
COMMENT ON TABLE outbox IS 'Class P, RLS-exempt: event routing and publication';
COMMENT ON TABLE inbox IS 'Class P: idempotent event receipt';
COMMENT ON TABLE roles IS 'Class H: system and tenant roles';
COMMENT ON TABLE role_permissions IS 'Class H: system and tenant role permissions';
COMMENT ON TABLE audit_events IS 'Class H: append-only tenant and platform audit';
COMMENT ON TABLE tenant_members IS 'Class T: tenant membership';
COMMENT ON TABLE role_assignments IS 'Class T: member role assignments';
COMMENT ON TABLE groups IS 'Class T: tenant groups';
COMMENT ON TABLE group_members IS 'Class T: tenant group membership';
COMMENT ON TABLE group_role_assignments IS 'Class T: group role assignments';
COMMENT ON TABLE tenant_apps IS 'Class T: app enablement';
COMMENT ON TABLE tenant_settings IS 'Class T: tenant settings';
COMMENT ON TABLE invitations IS 'Class T: tenant invitations';
COMMENT ON TABLE access_requests IS 'Class T: tenant access requests';

-- Bootstrap gives access_app broad default DML; this migration replaces it
-- with explicit grants and prevents future tables from inheriting broad DML.
ALTER DEFAULT PRIVILEGES FOR ROLE access_migrator IN SCHEMA public
  REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM access_app;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM access_app;
GRANT SELECT, INSERT, UPDATE ON tenants, apps, permissions, identities TO access_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON user_tenant_index TO access_app;
GRANT SELECT ON support_grants TO access_app;
GRANT INSERT ON outbox, inbox TO access_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON roles, role_permissions TO access_app;
GRANT SELECT, INSERT ON audit_events TO access_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_members, role_assignments,
  groups, group_members, group_role_assignments, tenant_apps, tenant_settings,
  invitations, access_requests TO access_app;
