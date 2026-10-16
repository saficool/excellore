-- WP-10: class P identities are read by the machine worker for drift counts only.
GRANT SELECT (user_id, status) ON identities TO access_worker;
