# ADR-0030: Access support grant event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires `access.supportgrant.created.v1` and `access.supportgrant.revoked.v1`. Appendix H specifies opaque tenant, grant, and staff user IDs and a required expiry. Staff access must be time-boxed and audited (INV-18), Access owns grant state (INV-08), and event payloads must contain no personal values (INV-11).

## Decision

Both events have typed C# records, Draft 2020-12 JSON Schemas, and examples. Parsers require nonempty IDs, matching envelope and payload tenant IDs, and an expiry timestamp with an explicit offset. Creation events require `expiresAt` later than the event time; revocation events accept a grant whose expiry has elapsed. Parsers and schemas reject known personal or authorization-bearing fields. Unrelated unknown fields remain allowed for forward compatibility.

Consumers parse before tenant work and use the durable inbox keyed by event ID. These events report a security-relevant fact but do not confer staff access; authorization still requires a current Access decision. The Access service must separately audit each grant change with actor, tenant, target, and reason (INV-12). The Access implementation, including the maximum grant duration and approvals, belongs to WP-11.

## Consequences

Security and notification consumers can react to grant changes without receiving names, email addresses, reason text, or role permissions. The strict ID and timestamp rules are proposed public contract details for owner review. No new endpoint, table, or permission is introduced.
