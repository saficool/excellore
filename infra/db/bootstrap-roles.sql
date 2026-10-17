\set ON_ERROR_STOP on
\getenv service DB_SERVICE
\getenv migrator_password DB_MIGRATOR_PASSWORD
\getenv app_password DB_APP_PASSWORD
\getenv publisher_password DB_PUBLISHER_PASSWORD
\getenv worker_password DB_WORKER_PASSWORD

-- Idempotent so dev-up.sh also repairs roles on existing local volumes.
SELECT format('CREATE ROLE %I LOGIN', :'service' || '_migrator')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'service' || '_migrator') \gexec
SELECT format('CREATE ROLE %I LOGIN', :'service' || '_app')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'service' || '_app') \gexec
SELECT format('CREATE ROLE %I LOGIN', :'service' || '_publisher')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'service' || '_publisher') \gexec
SELECT format('CREATE ROLE %I LOGIN', :'service' || '_worker')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'service' || '_worker') \gexec

SELECT format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD %L', :'service' || '_migrator', :'migrator_password') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD %L', :'service' || '_app', :'app_password') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD %L', :'service' || '_publisher', :'publisher_password') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD %L', :'service' || '_worker', :'worker_password') \gexec

SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database()) \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I, %I, %I, %I', current_database(), :'service' || '_migrator', :'service' || '_app', :'service' || '_publisher', :'service' || '_worker') \gexec
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT USAGE, CREATE ON SCHEMA public TO %I', :'service' || '_migrator') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'service' || '_app') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'service' || '_publisher') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'service' || '_worker') \gexec
