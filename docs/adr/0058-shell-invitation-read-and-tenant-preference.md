# ADR-0058: Shell invitation read and tenant preference

Status: proposed
Date: 2026-10-15
Owner: agent; needs human review

## Context

WP-07 requires the Shell BFF to show pending invitations, redirect to the last used tenant, and sign out of all apps. The Access API already lists invitations for a signed-in user, but the Shell BFF has only an Access machine credential, not an `api-access` user token. A browser preference must never become an authorization source. Keycloak backchannel logout may not reach another BFF during a local callback or network outage. INV-02, INV-05, INV-10, and INV-11 apply.

## Decision

Access exposes `POST /internal/v1/users/invitations:list` under the existing `Internal.Authz` machine policy. The Shell sends the session's opaque `sub` in the JSON body. Access looks up the verified, active identity in its mirror and returns only pending, unexpired invitations for that identity's email. The response omits email and code. The Shell links to the Access acceptance page with an opaque invitation ID; the one-time code remains separate.

The Shell stores the last used tenant slug in browser local storage as a navigation preference. At `/`, it matches the value against a fresh Access tenant list and uses only the matched link. Every launcher request still passes `requireTenant` and Access resolution; the preference grants no access. If local storage is unavailable, the picker remains usable.

All BFFs using the shared Valkey session store compare a per-subject logout marker when reading a session. Global sign-out changes the marker before deleting the initiating app's session. Existing sessions in Shell, Profile, and Access become invalid on their next read, even if Keycloak backchannel delivery fails. A newly created session reads the new marker and remains valid. The Valkey key uses a hash of the subject, carries no token, and expires after the absolute session lifetime. Keycloak end-session and verified backchannel handling remain in place.

## Consequences

The internal response is sensitive to the machine identity and is never cached. Shell availability depends on Access for the picker and launcher. Pending invitations require an existing verified identity mirror row; the acceptance page and code entry remain WP-08. All apps must share the same Valkey deployment for cross-app logout; if Valkey fails, session checks fail closed. Tests cover cross-user invitation isolation, POST body transport, rejection of a stale or invented tenant preference, and revocation across separate app stores.
