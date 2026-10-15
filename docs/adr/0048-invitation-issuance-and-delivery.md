# ADR-0048: Invitation issuance and delivery

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 needs tenant invitation creation and resend, provisioning owner delivery, and an Access-owned Keycloak admin client. The prior invitation URL fragment conflicts with INV-11. The platform owner chose an ID-only link with a separately entered one-time code and approved RFC-0006's narrow Keycloak action-link exception. Email existence must not leak through invitation responses. A pending invitation must remain recoverable after email or Keycloak failures.

## Decision

Access creates a random 256-bit invitation secret, stores only its hash, and sends an ID-only link and code in email text. Create and resend enforce the tenant gate, current `access.member.invite`, S1 and S3 role policies, group role checks, and step-up before mutation. Duplicate and existing-member invitation requests receive the same accepted response as a new invitation. Resend rotates the code; old codes cannot be accepted. A unique pending-email index prevents duplicate live invitations per tenant. `keycloak_user_id` is an optional opaque recovery field on class T `invitations`; existing forced RLS continues to apply.

Access's `svc-access-admin` client creates an unknown user by POST body, using an opaque invitation-derived username. On conflict it checks only that opaque username, with no email in a URL. A new user receives Keycloak required-actions email with a fixed redirect and a 24-hour action link. Existing users receive only Access email. Identity checks, action emails, delivery successes, and failures are audited with opaque IDs and machine-safe reason codes; invitation APIs retain a uniform response and allow resend. The platform operator can rotate and resend a provisioning owner's invitation through `POST /v1/platform/tenants/{id}/owner-invitation:resend`.

## Consequences

Invitation delivery depends on configured SMTP and `svc-access-admin` credentials. Email delivery is outside the database transaction; a delivery failure leaves a pending invitation for resend. The WP-08 Access UI must provide the acceptance page and code entry. The public acceptance body changes to `{ invitationId, oneTimeCode }`. INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, INV-13, and INV-16 apply.
