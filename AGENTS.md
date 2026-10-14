# AGENTS.md: Excellore

You are working on **Excellore**, a multi-tenant SaaS platform made of three apps (**Shell**, **Profile**, **Access**), their backend services, workers, and a Keycloak identity provider.

**The single source of truth is `excellore-platform-handbook.md` (The Excellore Platform Handbook).** Read **Part 0 (Agent Operating Manual)** before writing any code, then read the sections relevant to your task. If this file and the handbook disagree, the handbook wins.

---

## 1. Platform in 60 seconds

- **One global identity per person** (Keycloak, single realm `excellore`). A person can be a member of many **tenants** (customer organizations).
- **Keycloak tokens are tenant-agnostic.** They say who the person is (`sub`) and how strongly they authenticated (`acr`). They never carry tenant, role, or permission data.
- **Tenant authorization lives in the Access service** (the authority). Every API asks it "what may user U do in tenant T?" on each request (short cache, event invalidation).
- **Permissions** are strings like `access.role.manage`, declared in each service's `permissions.yaml` (code-first). **Roles** are system templates (`owner`, `admin`, `member`, `viewer`, `auditor`) plus tenant-defined custom roles. Permissions are additive; there are no deny rules.
- **App access has two gates:** the platform enables an app for a tenant, and the member must hold `<app>.app.access`.
- **Browser never sees tokens.** Each app has a Next.js BFF that holds tokens server-side and gives the browser an opaque `__Host-` session cookie.
- **Tenant context:** front-end `/t/{slug}/…`, API `/v1/tenants/{tenantId}/…`. Non-members get `404`.
- **Data isolation:** shared PostgreSQL, `tenant_id` on every tenant table, **forced row-level security**, transaction-local `set_config('app.tenant_id', …, true)`.
- **Services talk through events** (RabbitMQ). The only synchronous cross-app calls are Access's documented internal contracts.

## 2. Non-negotiables (full list: handbook section 0.3)

| ID | Rule |
|---|---|
| INV-01 | No tenant IDs, tenant roles, or permissions in Keycloak tokens, mappers, or claims |
| INV-02 | Tenant authorization comes only from the Access authority, per request. Never trust a header, body, cookie, or token for the tenant |
| INV-03 | Every tenant table has `tenant_id` and forced RLS. Runtime DB roles never have `BYPASSRLS`, superuser, or table ownership |
| INV-04 | Every `{tenantId}` route passes the tenant gate and a `RequirePermission` before any handler code |
| INV-05 | Browser never receives access or refresh tokens |
| INV-06 | One audience per API; reject other audiences |
| INV-07 | Workers and messages carry `tenantId`, run in tenant context, use machine identities, never carry tokens |
| INV-08 | Only Access changes tenant authorization data; permission catalog is code-first |
| INV-09 | Apps do not call each other's APIs or databases (events only, plus Access internal contracts) |
| INV-10 | Fail closed. Non-members get `404`, not `403` |
| INV-11 | No secrets, tokens, or personal data in logs, events, URLs, or errors |
| INV-12 | Audit every security-relevant change with `tenant_id`, actor, reason |
| INV-13 | Identity is global; membership is per tenant |
| INV-14 | Everything is code (realm, infra, schema, pipelines); no manual production changes |
| INV-15 | Standards only (OIDC, OAuth 2.0, JWT, RFC 9457, OpenTelemetry); no custom crypto or token formats |
| INV-16 | Every new tenant endpoint or table ships with a cross-tenant leakage test |
| INV-17 | New permissions are default-deny; they reach roles only via the catalog's `systemRoles` or explicit assignment |
| INV-18 | Platform staff are managed as code and see tenant data only through time-boxed, audited support grants. No impersonation |

If a requirement seems to conflict with an invariant, **stop and ask a human**. Never resolve it silently.

## 3. How to work

1. **Pick the next unfinished work package** in handbook section 34 (WP-00 to WP-15, in order). State which one you are doing.
2. Work in **small, reviewable commits on `main`**. Run all required local checks before each push, then verify CI on `main`. Use a short-lived `wp-<nn>/<short-description>` branch only when a PR is specifically required.
3. Use the **templates and shared libraries** (`Excellore.Auth`, `Excellore.Authz`, `Excellore.Platform`, `@excellore/auth`, `@excellore/ui`). If something security-relevant is missing, add it to the library, never to one app.
4. Write tests with the code. Meet the work package's **acceptance criteria** and the **definition of done** (handbook 0.6).
5. Quote the invariant IDs you touched in the commit body (or PR description when a PR is required).

### Commands

```bash
# Local stack
scripts/dev-up.sh                         # Keycloak, Postgres x3, Valkey, RabbitMQ, object store, Mailpit

# Checks (run before declaring a step done)
pnpm -w build && pnpm -w lint && pnpm -w test
dotnet build && dotnet test

# Realm verification (after WP-02)
scripts/check-realm.sh
```

### Definition of done (every change)

- [ ] Builds, lints, and type-checks; unit and integration tests pass
- [ ] Cross-tenant leakage test for every new tenant-scoped endpoint and table
- [ ] Every new permission is in `permissions.yaml` with granted and denied tests
- [ ] Every new event has a schema in `Excellore.Contracts` and an idempotency test
- [ ] No secret, token, or personal data in code, logs, fixtures, or events
- [ ] Migrations are backward compatible and include RLS for new tenant tables
- [ ] Audit events written for security-relevant changes
- [ ] Handbook or an ADR updated if behavior or a contract changed

## 4. When the handbook is silent

1. Search the handbook (invariants, section 36 defaults, glossary, appendices).
2. If section 36 gives a **binding default**, use it.
3. If the choice is low-risk and reversible, pick the simplest option consistent with the handbook and note it in the commit body (or PR).
4. If it affects security, tenant isolation, data models, public contracts, or cost: write a **proposed ADR** in `docs/adr/`, implement the **safest** option, and flag it for human review.
5. If an invariant conflicts with a requirement, stop and ask.

### Accepted defaults (handbook section 36)

- Tenants are created by platform operators; people join by invitation. **Self-serve signup is off.**
- Tenant deletion grace period: **14 days**.
- Authority unreachable: **reads may use cached permissions up to 5 minutes; writes fail closed (`503`)**.
- Max custom roles per tenant: 25. Groups: data model and API now, UI later.
- Single region. Pooled database tenancy; no silo databases yet. No custom tenant domains. No per-tenant enterprise SSO yet.
- Messaging: plain RabbitMQ client behind an `IEventBus` abstraction. Sessions and caches: Valkey.
- **Retention and compliance (Q-11) are a placeholder.** Do not build retention schedules, archival, or automatic purging. Keep audit data append-only. Only implement the deletion flows the handbook already specifies.

## 5. Stack

| Layer | Choice |
|---|---|
| Identity | Keycloak (single realm), OIDC, Authorization Code + PKCE, client credentials |
| Front end and BFF | Next.js (App Router), React, TypeScript (strict), Tailwind |
| Backend | ASP.NET Core, EF Core + Npgsql, FluentValidation, Serilog |
| Workers | .NET Worker Service, Quartz.NET |
| Data | PostgreSQL 16+ (RLS), Valkey, RabbitMQ, S3-compatible object storage |
| Tooling | pnpm + Turborepo, Docker Compose locally, OpenTofu and Kubernetes for production |
| Observability | OpenTelemetry, Prometheus, Grafana, Loki, Tempo |

Check the license list (handbook section 35) before adding any dependency. Several .NET libraries (MassTransit newer majors, MediatR, AutoMapper) are commercial; do not add them.

## 6. Repository layout

```
apps/            shell, profile, access            (Next.js UI + BFF)
packages/        excellore-auth, ui, config        (shared TypeScript)
services/        profile, access                   (Excellore.<App>.Api / Worker / Application / Domain / Infrastructure)
shared/dotnet/   Excellore.Auth, .Authz, .Platform, .Contracts
infra/           keycloak/, platform/, db/, tofu/, k8s/, compose/
docs/            handbook.md, adr/, runbooks/
```

## 7. Conventions

- **Names:** handbook Appendix E. Permission keys `<app>.<resource>.<action>`; events `<domain>.<entity>.<verb>.v<N>`; DB roles `<app>_migrator` (owns schema, migrations only) and `<app>_app` (runtime, no bypass).
- **API routes:** `/v1/tenants/{tenantId}/…` (tenant), `/v1/me/…` (user-level), `/v1/platform/…` (platform), `/internal/v1/…` (Access internal, machine tokens only).
- **Errors:** RFC 9457 problem details with stable `urn:excellore:problem:<name>` types. `404` for unknown or non-member tenant, `403` for member without permission or step-up needed.
- **Commits:** Conventional Commits with project scope, for example `feat(access-api): add custom role endpoints`.
- **Table classes:** every new table is classified in its migration as P (platform), H (hybrid), or T (tenant). T and H tables get forced RLS. Platform tables that carry `tenant_id` must be added to `infra/db/rls-exempt.txt`, which requires security review.
- **Security review record** is required for `infra/keycloak/`, `Excellore.Auth`, `Excellore.Authz`, `Excellore.Platform`, `@excellore/auth`, `services/access/`, every `permissions.yaml`, and `infra/db/rls-exempt.txt`. While @saficool is the only maintainer, the author records the review in the commit body (or PR when required); no separate approving reviewer is required (handbook section 24.2; ADR-0017).

## 8. Never

- Never weaken, skip, or delete a test, security header, or check to make a build pass.
- Never put tenant data in Keycloak tokens, or read the tenant from a header, body, or cookie.
- Never add a table without classification and RLS where required.
- Never store or log tokens, passwords, secrets, or full cookies.
- Never call another app's API or database, or hand-copy security code between apps.
- Never hard-code secrets, hostnames, or tenant data.
- Never touch production, real secrets, or shared environments.
- Never "temporarily" disable a control. If a control blocks you, the control or the design needs a human decision.

## 9. Ask a human for

Real secrets, domains and DNS, production access, legal or compliance decisions, any change to an invariant, and anything in section 4 steps 4 and 5.
