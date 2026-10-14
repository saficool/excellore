# ADR-0031: Global Profile event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires three Profile events. RFC-0005 records the owner's decision that their account, export, and deletion flows are global. Appendix H requires opaque user and export IDs and changed field names only. INV-07, INV-11, INV-13, and INV-16 apply.

## Decision

`profile.updated.v1`, `profile.deletion.requested.v1`, and `profile.export.ready.v1` use source `excellore/profile` and omit envelope `tenantid`. Their typed records and Draft 2020-12 JSON Schemas require nonempty opaque IDs. `profile.updated.v1` requires a nonempty, unique `changed` list bounded to `displayName`, `timeZone`, `avatar`, and `preferences`, which are global Profile fields. Identity fields use identity lifecycle events; tenant member profile fields need a separate tenant-scoped event in a later version. Parsers and schemas reject known personal values, object keys, and download URLs while allowing unrelated unknown fields for forward compatibility.

Consumers use the durable inbox keyed by envelope ID with no tenant context. None of these events authorizes tenant work or includes an export link.

## Consequences

Notification consumers receive an opaque export ID, not a signed URL or object key. The changed-field vocabulary and explicit forbidden-field list are proposed contract details for owner review. No new table, endpoint, or permission is introduced.
