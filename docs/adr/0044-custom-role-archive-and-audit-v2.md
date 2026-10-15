# ADR-0044: Custom role archive and versioned Access audit hashes

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 permits tenant custom roles to be created, edited, and deleted. Historical role assignments reference role IDs, so physically deleting a role would lose evidence or violate foreign keys. The initial Access audit hash included only coarse event fields; `before`, `after`, and `acr` were not bound to the hash. The handbook requires append-only audit, before/after values, MFA evidence, and authorization invalidation after role changes (INV-02, INV-03, INV-08, INV-11, INV-12).

## Decision

Custom role deletion archives the row with `deleted_at` after confirming that no active direct or group assignment uses it. Archived roles are excluded from role reads, authorization resolution, and the active-name uniqueness index. Historical assignments remain linked to the archived row. Only active custom roles count toward the plan limit, capped at the handbook default of 25.

The Access audit writer marks new records `hash_version = 2` and hashes the canonical stored `before` and `after` JSON, `acr`, and the original identity, action, target, reason, and timestamp fields. Existing records remain version 1 and retain their original hashes; they are not rewritten. A database trigger rejects update, delete, and truncate of audit events. Access writers use a transaction advisory lock to serialize the hash chain; role writes take this lock before tenant and role row locks. The audit record, role change, tenant authorization version increment, `access.authz.changed.v1`, and the informational `access.role.{created,updated,deleted}.v1` event enter one transaction. Role events carry only tenant and role IDs.

## Consequences

The migration must run before the updated Access API because the role readers reference `deleted_at`. Version 2 hashes are independently recomputable from stored fields, while older version 1 records remain linked in the chain under the earlier hash format. The version 2 format does not cover currently unused metadata columns such as source IP or support grant ID; populating those fields will require a new hash version. The shared global audit lock serializes Access audit writes across tenants and could become a throughput limit. The integration test covers tenant isolation, escalation, MFA, plan limits, archive and name reuse, role event contracts, hash verification, and append-only enforcement. This proposed security and data-model decision needs owner review.
