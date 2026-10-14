# ADR-0024: Global identity event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires C# records and JSON Schemas for the section 16.3 event catalog. Global identity events omit `tenantid` under section 16.2. RFC-0001 resolves the Appendix H email conflict. INV-07, INV-11, INV-13, and INV-16 apply.

## Decision

The shared envelope uses nullable `TenantId`: tenant events require a nonempty ID; global identity events require it to be absent or null. Five versioned identity payload records contain an opaque `userId`; the update event also contains a nonempty, unique list of allowed changed field names. The parser and schemas reject known personal-data values (`email`, `emailVerified`, `givenName`, `familyName`, `locale`) in `data`. Unknown unrelated fields are ignored for forward compatibility. Parsers emit a fixed error on malformed input.

Consumers use the envelope ID in the durable inbox, with no tenant context for global events. A global event does not grant tenant access or authorize any tenant work.

## Consequences

The contracts can be used by Access producers and service consumers in later work packages. Consumer handlers must resolve current identity data through an authorized contract if needed, and must not copy personal values into these events. The parser's explicit forbidden-field list needs updating if the identity event vocabulary grows.
