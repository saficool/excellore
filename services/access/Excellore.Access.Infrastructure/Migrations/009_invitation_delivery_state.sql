-- [T] invitations: retain only the opaque Keycloak user ID for resend recovery.
ALTER TABLE invitations ADD COLUMN IF NOT EXISTS keycloak_user_id uuid;
CREATE UNIQUE INDEX IF NOT EXISTS invitations_one_pending_email_uq
  ON invitations (tenant_id, lower(email)) WHERE status = 'pending';
