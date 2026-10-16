-- [T] Worker builds and finalizes tenant exports under forced RLS.
GRANT UPDATE (status, object_key, ready_at) ON tenant_exports TO access_worker;
GRANT SELECT ON tenant_apps TO access_worker;
