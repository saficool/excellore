-- [H] audit_events: preserve existing version-1 hashes and mark future complete hashes.
ALTER TABLE audit_events ADD COLUMN IF NOT EXISTS hash_version smallint NOT NULL DEFAULT 1;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'audit_events_hash_version_check') THEN
    ALTER TABLE audit_events ADD CONSTRAINT audit_events_hash_version_check
      CHECK (hash_version IN (1, 2));
  END IF;
END $$;

CREATE OR REPLACE FUNCTION access_audit_append_only_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'Audit events are append-only' USING ERRCODE = '55000';
END $$;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'audit_events_append_only') THEN
    CREATE TRIGGER audit_events_append_only
      BEFORE UPDATE OR DELETE OR TRUNCATE ON audit_events
      FOR EACH STATEMENT EXECUTE FUNCTION access_audit_append_only_guard();
  END IF;
END $$;
