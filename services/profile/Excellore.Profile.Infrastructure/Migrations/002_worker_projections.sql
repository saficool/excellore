-- WP-10: profile_worker projects tenant membership events under forced RLS.
GRANT SELECT, INSERT ON inbox TO profile_worker;
GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_member_profiles TO profile_worker;
