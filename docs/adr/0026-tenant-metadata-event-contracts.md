# ADR-0026: Tenant metadata event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires the catalog's tenant metadata and app availability events. RFC-0002 records the owner's decision to remove identifying and free-form values from these events. INV-07, INV-08, INV-11, and INV-16 apply.

## Decision

The shared contracts and Draft 2020-12 schemas define four tenant events: created, updated, app enabled, and app disabled. All require matching nonempty tenant IDs in envelope and payload. An update carries a nonempty, unique list of `slug`, `name`, or `plan` field names, never values. App events require a 1–64 character lowercase ASCII `appId` with letters, digits, `_`, or `-`. The parser and schemas reject known metadata-value fields (`slug`, `previousSlug`, `name`, `plan`, `apps`, `features`) in `data`; unrelated unknown fields remain allowed for forward compatibility.

Consumers parse events before entering tenant context, use the durable inbox for duplicate suppression, and fetch current values only through an authorized Access contract. Slug cache invalidation uses a tenant-ID reverse index or clears the namespace.

## Consequences

The events no longer expose identifying tenant metadata. Consumers may make an additional authorized call to fetch current state. A namespace clear can temporarily increase cache misses. The changed-field vocabulary and `appId` syntax are public contract choices proposed for owner review. Later producers must never add free-form values to these event payloads.
