# RFC-0006: Keycloak onboarding action link exception

Status: accepted by platform/security owner @saficool on 2026-10-15

## Problem

Handbook INV-11 forbids tokens in URLs, including the invitation token previously placed in a URL fragment. New invited people have no password, so ordinary OIDC login cannot establish their identity before Keycloak's required actions run. Keycloak's execute-actions email uses an action token in an HTTPS URL. The owner explicitly chose a separate Excellore invitation code and approved a narrow exception for Keycloak's action link.

## Decision

Excellore invitation links contain only an opaque invitation ID. A 256-bit one-time code is delivered in email text for manual entry after sign-in; only its hash is stored. The acceptance API receives the ID and code in a POST body. Access creates an unknown Keycloak identity with VERIFY_EMAIL, UPDATE_PASSWORD, and CONFIGURE_TOTP required actions and requests Keycloak's time-limited execute-actions email. Its redirect is a fixed Access acceptance page with no invitation code or email in the redirect URL.

INV-11 permits only Keycloak's own HTTPS execute-actions action token in its new-user onboarding URL. The link expires after 24 hours, must not be logged or forwarded by Excellore, and must not be reused to carry an Excellore invitation code. Existing identities receive no Keycloak action email. This exception does not permit tokens, secrets, or personal data in any other URL, event, log, or error.

## Consequences

New invitees receive both an Access invitation email and a Keycloak required-actions email. The Access invitation remains valid for seven days; the Keycloak action link has a shorter lifetime and may need to be resent. Operators can resend a provisioning owner invitation, and authorized tenant members can resend an ordinary pending invitation. WP-08 must supply the acceptance page and code-entry UX before end-to-end onboarding is available. The exception is recorded in the handbook INV-11 line and announced in its 5.0 change log.
