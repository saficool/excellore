-- WP-12: tenant deletion dispatch is platform metadata [P].
-- The tenant remains pending_deletion until both services confirm erasure.
ALTER TABLE tenants ADD COLUMN deletion_started_at timestamptz;
CREATE INDEX tenants_deletion_due ON tenants (deletion_due_at, id)
  WHERE status = 'pending_deletion' AND deletion_started_at IS NULL;
GRANT UPDATE (deletion_started_at) ON tenants TO access_worker;
COMMENT ON COLUMN tenants.deletion_started_at IS
  'Class P: worker dispatch marker; final deletion requires service confirmations';
