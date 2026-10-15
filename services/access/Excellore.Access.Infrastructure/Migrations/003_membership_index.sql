-- Class P, RLS-exempt lookup index maintained atomically with the class T
-- membership row. No new table is introduced by this migration.
CREATE OR REPLACE FUNCTION access_sync_membership_index() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    DELETE FROM user_tenant_index
    WHERE user_id = OLD.user_id AND tenant_id = OLD.tenant_id;
    RETURN OLD;
  END IF;

  IF TG_OP = 'UPDATE' AND
     (NEW.user_id, NEW.tenant_id) IS DISTINCT FROM (OLD.user_id, OLD.tenant_id) THEN
    DELETE FROM user_tenant_index
    WHERE user_id = OLD.user_id AND tenant_id = OLD.tenant_id;
  END IF;

  INSERT INTO user_tenant_index (user_id, tenant_id, membership_status)
  VALUES (NEW.user_id, NEW.tenant_id, NEW.status)
  ON CONFLICT (user_id, tenant_id)
  DO UPDATE SET membership_status = EXCLUDED.membership_status;
  RETURN NEW;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tenant_members_index_sync') THEN
    CREATE TRIGGER tenant_members_index_sync
      AFTER INSERT OR UPDATE OR DELETE ON tenant_members
      FOR EACH ROW EXECUTE FUNCTION access_sync_membership_index();
  END IF;
END $$;
