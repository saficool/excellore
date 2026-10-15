-- [T] tenant_members: stable opaque membership identifier for lifecycle events.
ALTER TABLE tenant_members ADD COLUMN IF NOT EXISTS membership_id uuid NOT NULL DEFAULT gen_random_uuid();
CREATE UNIQUE INDEX IF NOT EXISTS tenant_members_membership_id_uq ON tenant_members (membership_id);
