# ADR-0076: Hybrid notification delivery boundary

Status: proposed; safest option selected for owner review

## Context

Excellore needs reusable business notifications. A general synchronous email API would couple every
application to another service and create an internal data-exfiltration capability. Putting email
addresses, phone numbers, rendered content, or invitation codes on RabbitMQ would violate INV-11.
Only Access holds a newly issued invitation code in plaintext, and Keycloak owns its action tokens.

## Decision

Create an independent Notifications service. It consumes allow-listed business events containing
opaque IDs, resolves verified recipient contact through a documented Access internal contract, and
owns template selection, channel policy, delivery state, retries, suppression, and provider adapters.

Access keeps synchronous invitation-code delivery and Keycloak keeps identity-provider email. Both
use separate provider credentials. Application services cannot submit arbitrary recipients or
content. Email is the first active channel; SMS, push, and in-app interfaces remain inactive until
their endpoint, consent, compliance, abuse, and cost controls are approved.

## Consequences

Normal application work remains independent of Notifications availability because notifications are
event-driven. Contact data does not enter platform events. Access remains a dependency when the
worker resolves a verified contact; failures retry without rolling back the originating business
operation. Security emails are not fully centralized, which avoids transmitting ephemeral secrets
and leaves INV-09 and INV-11 unchanged.

The service adds a database, worker, provider credentials, delivery monitoring, erasure/export
handlers, and tenant-isolation tests. INV-03, INV-07, INV-09, INV-11, INV-13, INV-14, and INV-16 apply.
