\set ON_ERROR_STOP on
\getenv service DB_SERVICE

-- Run as the service migrator. PostgreSQL permits a role to change only its own
-- default privileges unless the caller is a member or superuser. Neon project
-- owners are deliberately not made members of Excellore service roles.
SELECT :'service' IN ('access', 'notifications') AS restricted_service \gset
\if :restricted_service
SELECT format(
  'ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM %I',
  :'service' || '_app') \gexec
\else
SELECT format(
  'ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I',
  :'service' || '_app') \gexec
SELECT format(
  'ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO %I',
  :'service' || '_app') \gexec
\endif
