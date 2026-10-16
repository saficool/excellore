# ADR-0063: Worker database boundaries and tenant expiry

Status: proposed
Date: 2026-10-16
Owner: agent; owner approved the scoped roles, authorization version update, and membership trigger change

## Context

WP-10 needs scheduled Access expiry and Profile event projection. The existing publisher roles can only read and mark outbox rows. Giving them tenant mutation rights would mix publication with authorization changes. The owner approved separate, narrowly granted machine database roles. The existing Access membership trigger used an index INSERT even for a status-only update, which would have required a broader worker grant on an RLS-exempt platform table.

## Decision

`access_worker` and `profile_worker` are distinct non-owner, non-superuser, `NOBYPASSRLS` logins provisioned by code. Access worker grants cover tenant listing, expiry columns on assignments and support grants, support membership status, the existing membership index status, append-only audit inserts, outbox inserts, and tenant authorization version increments. Profile worker grants cover the inbox and tenant member profile projection. Neither role can read global identities or user profiles. Access uses transaction-local `app.tenant_id` for each tenant, a session advisory lock for the sweep, and a bounded tenant scheduler. Profile uses the same tenant context for inbox and projection in one transaction.

The Access membership index trigger now updates an existing index row for a same-member status change and fails if it is missing. The worker needs no index INSERT grant. The change leaves insert and key-change behavior with the existing trigger path. Expiry writes an append-only audit record, an outbox event, and one `authz_version` bump in the same transaction; event actors identify a machine and audit actors use the schema's `service` type. Profile's global outbox uses a dispatcher mode that selects no tenant column because the Profile outbox schema has none.

One-time invitation delivery remains synchronous. The invitation code is never recoverable from the stored hash and must not be placed in a RabbitMQ event or worker log. Background email is limited to notifications without one-time secrets.

## Consequences

Local setup gains generated worker credentials and a stable, locally generated, non-secret Access machine actor ID. Disposable PostgreSQL tests prove wrong-context reads return no tenant rows, duplicate event delivery is idempotent, and expiry does not affect another tenant. The RabbitMQ dead-letter test remains in the shared platform suite. INV-03, INV-07, INV-10, INV-11, INV-12, and INV-16 apply.
