# RFC-0007: Notifications platform

Status: proposed; owner-directed implementation started 2026-10-17

## Problem

Future Excellore applications need email, SMS, push, and in-app notifications without copying
provider integrations or turning Access into a general communications service. INV-09 requires
applications to react through events, while INV-11 prohibits personal contact data and arbitrary
message content in those events. Invitation codes are deliberately available only in plaintext
during Access issuance and therefore cannot be moved through the broker.

## Proposal

Create an independent Notifications service and worker. Applications publish existing, versioned
business facts containing only opaque identifiers and bounded safe codes. Notifications maps an
allow-listed fact to a versioned template and channel policy, resolves a verified recipient through
a documented Access internal contract, and performs delivery. It never accepts arbitrary subjects,
bodies, email addresses, or phone numbers from application events.

Access continues to deliver its one-time invitation code synchronously using the shared hardened
SMTP transport. Keycloak continues to deliver identity-provider messages. This keeps each ephemeral
security secret inside its owning boundary and requires no new INV-09 exception.

The first release activates email through Mailpit locally and an authenticated SMTP relay in a
deployed environment. SMS, push, and in-app interfaces are reserved, but paid delivery stays off
until verified endpoint enrollment, consent, abuse controls, provider cost limits, and applicable
sender-registration requirements are decided.

## Security properties

- Tenant business events carry tenant ID and opaque user ID, never personal contact data.
- Notification tables are tenant-scoped and forced through RLS.
- Recipient resolution uses a machine token, POST body, private network, and verified active
  membership checks.
- Templates and event-to-template mappings are code-defined and versioned.
- Delivery logs and telemetry contain safe status codes and opaque IDs only.
- Invitation and Keycloak action links retain the existing INV-11 boundaries.

## Rollout

WP-15 adds the shared transport, Notifications database and worker, recipient resolution, local
Mailpit integration, authenticated SMTP configuration, and one end-to-end business notification.
Additional channels are enabled through later reviewed ADRs.
