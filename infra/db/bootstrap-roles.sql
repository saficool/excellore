\set ON_ERROR_STOP on
\getenv service DB_SERVICE
\getenv migrator_password DB_MIGRATOR_PASSWORD
\getenv app_password DB_APP_PASSWORD

-- Idempotent so dev-up.sh also repairs roles on existing local volumes.
SELECT format('CREATE ROLE %I LOGIN', :'service' || '_migrator')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'service' || '_migrator') \gexec
SELECT format('CREATE ROLE %I LOGIN', :'service' || '_app')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'service' || '_app') \gexec

SELECT format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD %L', :'service' || '_migrator', :'migrator_password') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD %L', :'service' || '_app', :'app_password') \gexec

SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'service') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I, %I', :'service', :'service' || '_migrator', :'service' || '_app') \gexec
SELECT format('GRANT USAGE, CREATE ON SCHEMA public TO %I', :'service' || '_migrator') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'service' || '_app') \gexec
-- Access uses table-specific grants from its migration. Re-running dev-up must
-- not restore broad default grants for later Access migrations.
SELECT :'service' = 'access' AS access_service \gset
\if :access_service
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM %I', :'service' || '_migrator', :'service' || '_app') \gexec
\else
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', :'service' || '_migrator', :'service' || '_app') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO %I', :'service' || '_migrator', :'service' || '_app') \gexec
\endif
