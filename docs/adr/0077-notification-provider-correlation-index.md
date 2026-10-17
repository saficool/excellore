# ADR-0077: Opaque notification provider correlation index

Status: accepted; owner approved 2026-10-17

## Context

Resend signs delivery webhooks and includes the RFC `Message-ID`, but an SMTP send does not provide
the Resend email ID before the callback. The Notifications API must map a callback to a forced-RLS
delivery row and enter the correct tenant context. Putting the tenant ID or delivery ID in the
outbound `Message-ID` would expose internal identifiers to the provider and recipient. Scanning the
tenant table without RLS is prohibited.

## Decision

Generate a cryptographically random opaque correlation UUID when the worker creates a delivery.
Send only that random value in the email `Message-ID`. In the same transaction, write an immutable
class-P index row mapping correlation ID to tenant ID and delivery ID. The index contains no address,
name, subject, rendered content, provider payload, or secret.

The Notifications API verifies the Resend signature over the raw request body with Resend's official
MIT-licensed .NET verifier. It parses only the provider event type, provider email ID, event time,
and opaque message correlation. It reads the immutable index, sets the transaction-local tenant
context, and updates only provider-status columns on the matching forced-RLS delivery row. A separate
class-P receipt table stores the provider name and signed webhook message ID for idempotency. The raw
payload and its personal fields are neither stored nor logged.

Because the immutable correlation index carries `tenant_id`, it must be explicitly listed in
`infra/db/rls-exempt.txt`. The `notifications_worker` role may insert and read index rows. The
`notifications_app` role may read the index, insert webhook receipts, and update only the bounded
provider-status columns on tenant delivery rows. Neither role may update or delete index rows.

Production exposes only the exact `/webhooks/v1/resend` path through a dedicated HTTPS host. The
ingress disables access logs, the API caps request bodies, and all other Notifications API paths
remain private.

## Consequences

Provider and recipient see only a random correlation value. Webhooks can enter the correct RLS
context without a broad scan, a security-definer function, `BYPASSRLS`, or tenant identifiers in
email headers. The index is security-sensitive metadata and grows with deliveries; automatic purge
is deferred while Q-11 retention remains undecided. Tests must prove invalid signatures are denied,
duplicate callbacks are idempotent, unknown correlations reveal nothing, and a correlation mapped to
one tenant cannot update another tenant's delivery.

INV-03, INV-07, INV-11, INV-14, and INV-16 apply.
