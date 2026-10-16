# ADR-0066: Platform app metadata in a tenant transaction

Status: proposed
Date: 2026-10-16
Owner: agent; owner approved the scoped handler design

## Context

WP-11 asks platform operators to enable a tenant app. `tenant_apps` is a class T table with forced RLS, while the platform console normally uses class P metadata only. Reading or updating that row without a tenant context is denied. The owner approved a narrow platform operator flow that enters the target tenant context for app-enable metadata; tenant content still requires a support grant.

## Decision

`PlatformTenantService` checks a Keycloak `platform_roles` policy at the API boundary and requires recent MFA for mutations. It opens a transaction, sets the target `app.tenant_id` transaction-locally, and reads or changes only the named tenant's `tenant_apps` row. It never joins tenant member or content tables. The change writes tenant-scope and platform-scope audit records, increments `authz_version`, and emits the existing app status event when status changes. The core app cannot be disabled. Forced RLS and the runtime role remain in place. Tests use two tenants and a wrong-context database update.

Tenant create continues to use scoped writes for its initial app rows. Ordinary staff access to tenant content continues to require an approved, time-boxed support grant. Emergency Keycloak force sign-out and user disable are deferred: its Admin API embeds a user ID in the backend URL, conflicting with INV-11, and the owner chose deferral instead of a new exception.

## Consequences

Platform staff can manage app enablement without tenant content permissions. The platform console may show app status and bounded feature metadata as operator metadata. Production staff assignments still require independently approved code changes; local fixture assignments are generated from `platform-staff.yaml` and checked by CI.

Invariants: INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, INV-16, INV-18.
