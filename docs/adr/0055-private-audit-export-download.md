# ADR-0055: Private audit export download

Status: Proposed (owner direction recorded 2026-10-15)

## Context

Handbook section 15.6 called for a signed audit export link. Such a URL contains a bearer token, contrary to INV-11. Audit exports can contain sensitive tenant records.

## Decision

WP-06 records a tenant-scoped export request under forced RLS and returns only an opaque job ID and status. WP-10 will write the export to private object storage and expose it through an authenticated, tenant-gated Access BFF download stream. No signed URL is returned or logged. The object key is never returned to the browser. The download requires `access.audit.read` at request time, so revocation takes effect.

## Consequences

WP-10 processes the request under forced RLS, stores newline-delimited audit JSON in a private object, and marks the job ready in the Access database. The Access API requires the tenant gate, `access.audit.read`, and recent MFA before looking up the opaque key. The BFF opts into only `application/x-ndjson` for this route and streams at most 64 MiB with `no-store` and `nosniff` headers. Exports exceeding that bound are marked failed and audited; larger export packaging remains a follow-up. The browser receives neither the object key nor a signed URL. INV-02, INV-03, INV-04, INV-10, INV-11, INV-12, and INV-16 apply.
