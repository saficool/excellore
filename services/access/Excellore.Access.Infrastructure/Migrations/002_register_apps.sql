-- Code-first app registration from infra/platform/app-catalog.yaml.
-- Idempotent so local development can rerun it after the initial migration.
INSERT INTO apps (id, name, core) VALUES
  ('access', 'Access', true),
  ('profile', 'Profile', true)
ON CONFLICT (id) DO NOTHING;
