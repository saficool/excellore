-- [P, RLS-exempt] outbox publication is isolated from the Access API role.
-- The login is created by environment-owned bootstrap or production IaC.
GRANT SELECT, UPDATE (published_at) ON outbox TO access_publisher;
