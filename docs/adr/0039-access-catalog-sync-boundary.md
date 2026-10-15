# ADR-0039: Access catalog sync and removal boundary

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-05 requires a code-first permission catalog, system roles, an authenticated sync endpoint, and idempotent updates. New permissions are default-deny except for explicit `systemRoles` grants (INV-17). Removing a permission must be refused while any system or tenant custom role still holds it. Access runtime queries cannot inspect every tenant's custom-role grants from platform scope because forced RLS intentionally limits them to one tenant context.

## Decision

The `svc-catalog-sync` machine identity calls `PUT /internal/v1/catalog/{app}` with an `api-access` token carrying `access.catalog.sync`. The endpoint validates the app and permission definitions, updates only the matching app's catalog and global system-role mappings in one transaction, records a platform audit entry, increments every tenant's `authz_version`, and writes one `access.authz.changed.v1` outbox row per tenant. An unchanged catalog writes nothing. Tenant custom-role mappings are never changed by sync.

Every active permission explicitly grants the `owner` system role; other roles receive it only when listed. Deprecated permissions may clear their system-role grants. Sync refuses removal of **any** existing permission, even after deprecation. A future reviewed migration and deletion procedure must prove no tenant custom role still references the permission before enabling removal. Runtime `access_app` does not receive permission-table DELETE rights.

The platform audit hash is SHA-256 over the previous hash followed by a UTF-8 record containing the new event ID, UTC timestamp, actor ID, action, target, and reason. The catalog transaction uses one advisory lock, so its platform audit entries are serialized. The broader audit format will be finalized with WP-06.

## Consequences

INV-03, INV-06, INV-08, INV-10, INV-11, INV-12, INV-14, and INV-17 apply. CI validates `permissions.yaml`; database tests cover initial grants, unchanged reruns, deprecation, custom-role preservation, removal refusal, audit entries, and invalidation outbox rows. The initial app registration comes from `infra/platform/app-catalog.yaml` through `002_register_apps.sql`.

Automatic approval review rejected a proposed runtime cross-tenant scan for permission deletion because it would let `access_app` set arbitrary tenant contexts and weaken forced-RLS isolation. This proposed ADR records the fail-closed behavior for owner review. Production catalog publication belongs in the deployment pipeline after WP-14 supplies its service credentials and internal network path.
