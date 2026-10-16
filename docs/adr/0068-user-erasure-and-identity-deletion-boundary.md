# ADR-0068: User erasure with deferred Keycloak deletion (proposed)

## Context

Keycloak's Admin API identifies a user in a server-to-server URL. INV-11
currently permits only the new-user execute-actions link in a URL. The owner
deferred Keycloak identity deletion rather than extending that exception.
Access audit rows are append-only and hash-chained; rewriting user references
in them would invalidate the chain.

## Decision

Application data erasure can remove global Profile data and anonymize or
remove live tenant references. Restricted original Access audit rows remain
unchanged and receive an appended erasure marker. The request remains
**pending external identity deletion** while the Keycloak identity exists.
Workers must not publish `identity.user.deleted.v1` or report complete global
identity deletion until Keycloak deletion has actually succeeded under a
separately approved design.

## Consequences

An app-level tombstone must prevent an active Keycloak session from recreating
erased application data, and Access must deny tenant authorization for the
pending identity. Archived audit data remains accessible only to the
restricted migrator/security path. Complete identity deletion is blocked
until INV-11 and the Keycloak Admin API conflict is resolved.

INV-02, INV-03, INV-10, INV-11, INV-12, INV-13, INV-16.
