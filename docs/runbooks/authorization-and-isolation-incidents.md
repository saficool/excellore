# Authorization and tenant-isolation incidents

## Access authority outage

1. Confirm Access API readiness, Access database, Keycloak service tokens, and network policy.
2. Writes must fail with `503`. Reads may use previously cached permissions for at most five
   minutes; after that they fail closed. Do not extend TTLs or bypass the tenant gate.
3. Recover Access through the deployment or database-restore pipeline. Confirm authorization
   resolution and invalidation events before accepting writes.

## Suspected cross-tenant leak

1. Treat the report as a security incident. Disable affected public BFF ingress or suspend affected
   tenants through Access; choose the smallest boundary that reliably stops exposure.
2. Preserve deployment digests, database audit archives, request trace IDs, authorization decision
   metrics, and cloud audit records in restricted storage. Do not copy personal data into chat,
   tickets, URLs, or general logs.
3. Run forced-RLS verification and the relevant cross-tenant integration tests against an isolated
   restore. Check tenant gate ordering, cache generation, support grants, and worker tenant context.
4. Repair through code and pipeline, perform a second-person security review when another
   maintainer is available, and verify negative cross-tenant scenarios before reopening ingress.

## Emergency tenant suspension

Use the audited Access platform action with a reason and MFA. Confirm tenant authorization resolves
inactive, cached decisions are invalidated, app writes fail closed, and the Shell no longer offers
the tenant. Reactivation also requires an audited platform action and incident-owner approval.
