# WP-13 threat model review

Reviewed: 2026-10-16

Scope: handbook section 29.2, the Shell/Profile/Access BFFs, Access and Profile APIs and workers, Keycloak integration, PostgreSQL tenant isolation, Valkey authorization caching, RabbitMQ events, and private object storage.

| Threat area                                                  | Automated evidence                                                                         | Review result                                                                                 |
| ------------------------------------------------------------ | ------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------- |
| Cross-tenant IDOR, tenant confusion, RLS bypass              | Tenant route audit, API isolation harness, forced-RLS database tests                       | Covered                                                                                       |
| Stale permissions and confused deputy                        | Valkey invalidation tests, authority-outage tests, per-API audience tests                  | Covered                                                                                       |
| Role escalation and disabled-app bypass                      | Custom role, assignment, step-up, and effective-permission tests                           | Covered                                                                                       |
| Invitation, session, CSRF, redirect, and browser token theft | Invitation acceptance tests; PKCE/state/nonce, BFF proxy, logout, and CSP tests            | Covered                                                                                       |
| Support access abuse                                         | Support request, approval, visibility, audit, revocation, and expiry tests                 | Covered                                                                                       |
| Tenant enumeration and noisy neighbors                       | Per-user/per-tenant limits, anonymous probing detector, Prometheus alert                   | Covered; deployment-wide ingress boundary is WP-14                                            |
| Message replay and worker tenant confusion                   | Inbox idempotency, schema checks, dead-letter tests, tenant worker scope tests             | Covered                                                                                       |
| Account takeover and identity-provider compromise            | Keycloak realm checks, MFA and brute-force policy; monitoring rules                        | Covered; HA and recovery drills are WP-14                                                     |
| Supply chain                                                 | pnpm and NuGet audits, Gitleaks, Trivy filesystem scan, pinned CI scanner versions         | Application dependencies covered; image SBOM and signing belong to the WP-14 release pipeline |
| Tenant and user lifecycle residue                            | Tenant deletion, erasure receipt, tombstone, object deletion, and user anonymization tests | Covered; Keycloak identity deletion remains visibly deferred by ADR-0068                      |

The review found no accepted high-severity application risk. CI blocks high or critical dependency and filesystem findings. Infrastructure residual risks listed above belong to WP-14 and are not application-control exceptions.
