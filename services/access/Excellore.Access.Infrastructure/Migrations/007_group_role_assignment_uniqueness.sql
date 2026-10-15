-- [T] group_role_assignments: one live grant per group and role; historical revoked rows remain.
CREATE UNIQUE INDEX IF NOT EXISTS group_role_assignments_active_uq
  ON group_role_assignments (tenant_id, group_id, role_id)
  WHERE revoked_at IS NULL;
