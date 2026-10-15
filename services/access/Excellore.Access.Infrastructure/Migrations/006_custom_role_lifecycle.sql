-- [H] roles: retain retired custom roles referenced by historical assignments.
ALTER TABLE roles ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
DROP INDEX IF EXISTS roles_name_uq;
CREATE UNIQUE INDEX IF NOT EXISTS roles_name_active_uq
  ON roles (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name))
  WHERE deleted_at IS NULL;
