-- WP-10: class P user_profiles mirror fields; no tenant data is granted.
GRANT SELECT (user_id, deleted_at),
      UPDATE (mirror_first_name, mirror_last_name, mirror_locale, updated_at)
ON user_profiles TO profile_worker;
