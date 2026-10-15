# ADR-0042: Access outbox publisher role and worker

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-05 requires Access to publish its transactional outbox, including `access.authz.changed.v1`. ADR-0019 provides a shared confirmed dispatcher, but the Access worker is still a placeholder and the Access schema grants its runtime API role only INSERT on the class P outbox. The publisher must read across tenants without weakening tenant RLS or giving the API role publication rights.

## Decision

The local bootstrap creates a separate `<service>_publisher` login with a generated credential and no inherited privileges. Access migration 004 grants `access_publisher` only SELECT and UPDATE of `outbox.published_at`. The worker connects with this role, uses the shared `PostgresOutboxDispatcher` and `RabbitMqEventBus`, and keeps a confirmed publisher open across batches. Its RabbitMQ and database connection strings come from configuration. It retries failures with a bounded delay and logs only a generic warning, never event bodies, credentials, or exception text.

The publisher does not create a catch-all queue. It marks an event published only after RabbitMQ confirms routing to a provisioned subscriber queue, as ADR-0019 requires. Until a consumer binds `access.authz.changed.v1`, that event remains pending. Consumer inbox idempotency handles duplicate delivery after uncertain confirmation. The Access worker does not request a Keycloak token for this database-to-broker job; its dedicated database role and broker credential are its machine identities.

## Consequences

INV-02, INV-03, INV-07, INV-09, INV-10, INV-11, INV-14, and INV-16 apply. Restricted-role tests must prove the publisher cannot read tenant data or alter event payloads, and integration tests must prove broker failure leaves rows pending and a confirmed retry marks them published. This proposed security choice needs owner review.
