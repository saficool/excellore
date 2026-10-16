# Onboard an Excellore application

1. Add the app to `infra/platform/app-catalog.yaml`, its code-first `permissions.yaml`, and explicit
   system-role mappings. New permissions stay default-deny unless deliberately mapped.
2. Generate the BFF from the shared template. Register one tenant-agnostic OIDC client and one API
   audience; never add tenant, role, or permission claims to tokens.
3. Use `@excellore/auth`, `Excellore.Auth`, `Excellore.Authz`, and `Excellore.Platform`. Add tenant
   gate and `RequirePermission` before every tenant handler.
4. Classify every table and add forced RLS plus cross-tenant leakage tests. Add event schemas and
   idempotency tests for every event.
5. Add separate image, runtime Secret, service account, network policy, probes, metrics, dashboard,
   alerts, backup contribution, erasure contribution, and Helm/OpenTofu values.
6. Update the production manifest validator's exact public/private workload sets. Public ingress is
   allowed only for the new BFF.
7. Complete security review, load/security checks, staging deployment, restore contribution test,
   and rollback exercise before platform operators enable the app for a tenant.
