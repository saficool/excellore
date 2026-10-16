-- WP-10: access_worker is a machine-only role. T/H tables remain under forced RLS.
GRANT SELECT, UPDATE (authz_version, updated_at) ON tenants TO access_worker;
GRANT SELECT, UPDATE (revoked_at) ON role_assignments, group_role_assignments TO access_worker;
GRANT SELECT, UPDATE (status) ON support_grants TO access_worker;
GRANT SELECT, UPDATE (status, updated_at) ON tenant_members TO access_worker;
GRANT SELECT, UPDATE (membership_status) ON user_tenant_index TO access_worker;
GRANT SELECT, INSERT ON audit_events TO access_worker;
GRANT SELECT, UPDATE (status, object_key, ready_at) ON audit_exports TO access_worker;
GRANT INSERT ON outbox TO access_worker;

-- Existing membership status updates only change the existing index row.
-- The worker does not need INSERT on the RLS-exempt index.
CREATE OR REPLACE FUNCTION access_sync_membership_index() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    DELETE FROM user_tenant_index
    WHERE user_id = OLD.user_id AND tenant_id = OLD.tenant_id;
    RETURN OLD;
  END IF;

  IF TG_OP = 'UPDATE' AND
     (NEW.user_id, NEW.tenant_id) IS NOT DISTINCT FROM (OLD.user_id, OLD.tenant_id) THEN
    UPDATE user_tenant_index SET membership_status = NEW.status
    WHERE user_id = NEW.user_id AND tenant_id = NEW.tenant_id;
    IF NOT FOUND THEN
      RAISE EXCEPTION 'Membership index missing' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
  END IF;

  IF TG_OP = 'UPDATE' THEN
    DELETE FROM user_tenant_index
    WHERE user_id = OLD.user_id AND tenant_id = OLD.tenant_id;
  END IF;

  INSERT INTO user_tenant_index (user_id, tenant_id, membership_status)
  VALUES (NEW.user_id, NEW.tenant_id, NEW.status)
  ON CONFLICT (user_id, tenant_id)
  DO UPDATE SET membership_status = EXCLUDED.membership_status;
  RETURN NEW;
END $$;
