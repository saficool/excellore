# The Excellore Platform Handbook

**The single source of truth for the architecture, tenancy, identity, authorization, engineering standards, operations, and build plan of the Excellore multi-tenant SaaS platform. Written to be read by humans and by LLM coding agents.**

| | |
|---|---|
| **Document version** | 5.1 |
| **Status** | Draft for ratification (agents MAY build from it now; see Part 0) |
| **Audience** | LLM coding agents, engineers, architects, security reviewers, operators |
| **Supersedes** | v2.0 (single-tenant model with roles in Keycloak) |
| **Review cadence** | Quarterly, and on every architecture-impacting change |

> **Reading conventions.** **MUST / MUST NOT** are mandatory. **SHOULD / SHOULD NOT** are strong defaults that need a written reason to deviate from. **MAY** is optional. `INV-nn` marks a platform invariant (Part 0). `WP-nn` marks a work package (Part VII). `Q-nn` marks an open decision with a binding default (section 36). **(verify)** marks a fact that depends on a product version or license and MUST be re-checked at the moment of adoption.

### Change log

| Version | Summary |
|---|---|
| 5.1 | Member suspend, reactivate, and remove send opaque `userId` in request bodies instead of URLs (owner decision; ADR-0049 proposed). Reactivation rechecks current grants before restoring access. |
| 5.0 | Owner-approved narrow INV-11 exception for Keycloak's time-limited HTTPS execute-actions link during new-user onboarding. Excellore invitation URLs carry only opaque IDs; invitees enter a separate one-time code (RFC-0006, ADR-0048 proposed). |
| 4.10 | Shared app catalog resolves public app origins from environment configuration, validates links, and supplies the shared tenant/app header (ADR-0035 proposed). |
| 4.9 | Access user-scoped internal lookups send opaque user IDs in POST bodies instead of URLs; the BFF may resolve fresh on every request (owner decision; ADR-0034 proposed). |
| 4.8 | RP-initiated logout sends the ID token by server-side POST to Keycloak, never in a browser URL (owner decision; ADR-0033 proposed). |
| 4.7 | Erasure confirmations have concrete Access and Profile event types; tenant and user scopes require exactly one matching opaque target ID (ADR-0032 proposed). |
| 4.6 | Global Profile account, export, and deletion events omit tenant ID; Profile update events carry bounded changed field names only (RFC-0005). |
| 4.5 | Access role events carry only opaque tenant and role IDs; custom names and permission lists are omitted by owner decision (RFC-0004). |
| 4.4 | Membership-added events use opaque tenant, user, and membership IDs without role names, by owner decision (RFC-0003). |
| 4.3 | Tenant metadata events carry opaque IDs and bounded codes, without names, slugs, plans, or free-form features, by owner decision (RFC-0002). |
| 4.2 | Global identity lifecycle events carry opaque user IDs and changed field names only; the `identity.user.created.v1` email exception was removed by owner decision (RFC-0001). |
| 4.1 | Keycloak login and admin events remain in the Keycloak event store until a redacting export path is available (ADR-0004). |
| 4.0 | Solo-maintainer repository review policy accepted: owner security checklist and required CI replace mandatory independent PR approval until another maintainer joins (ADR-0003). |
| 3.1 | Defaults in section 36 accepted. Retention reduced to a placeholder (Q-11). |
| 3.0 | **Multi-tenant SaaS.** Tenant model, tenant-specific roles, permissions, and app access resolved by the Access authority (not Keycloak). Row-level-security data isolation. Agent operating manual, invariants, build plan with acceptance criteria, DDL and catalog appendices. |
| 2.0 | Apps named Shell, Profile, Access. Platform catalog, app specs, events, standards. |
| 1.0 | Initial authentication architecture (Shell, App A, App B). |

---

## Table of Contents

**[0. Agent Operating Manual](#0-agent-operating-manual)**

**Part I: Platform Foundations**
[1. Purpose and Scope](#1-purpose-and-scope) · [2. Principles](#2-principles) · [3. Platform Catalog](#3-platform-catalog) · [4. Architecture Overview](#4-architecture-overview) · [5. Decision Register](#5-decision-register)

**Part II: Multi-Tenancy**
[6. Tenancy Model](#6-tenancy-model) · [7. Authorization Model](#7-authorization-model) · [8. Data Isolation](#8-data-isolation)

**Part III: Identity Foundation**
[9. Domains, Hosts, and Cookies](#9-domains-hosts-and-cookies) · [10. Keycloak Configuration](#10-keycloak-configuration) · [11. Authentication Flows](#11-authentication-flows) · [12. Logout and Session Management](#12-logout-and-session-management)

**Part IV: Applications**
[13. Shell](#13-shell) · [14. Profile](#14-profile) · [15. Access](#15-access) · [16. Platform Events and Inter-App Communication](#16-platform-events-and-inter-app-communication) · [17. Onboarding a New App](#17-onboarding-a-new-app)

**Part V: Engineering Standards**
[18. Technology Stack](#18-technology-stack) · [19. BFF Standard](#19-bff-standard-nextjs) · [20. API Standard](#20-api-standard-net) · [21. Worker and Messaging Standard](#21-worker-and-messaging-standard) · [22. Data Standards](#22-data-standards) · [23. Frontend Standards](#23-frontend-standards) · [24. Repository, Branching, and Ownership](#24-repository-branching-and-ownership) · [25. Testing Strategy](#25-testing-strategy) · [26. CI/CD and Release](#26-cicd-and-release)

**Part VI: Operations**
[27. Infrastructure and Deployment](#27-infrastructure-and-deployment) · [28. Observability and SLOs](#28-observability-and-slos) · [29. Security](#29-security) · [30. Incident Response and Runbooks](#30-incident-response-and-runbooks) · [31. Backup and Disaster Recovery](#31-backup-and-disaster-recovery) · [32. Local Development](#32-local-development)

**Part VII: Build Plan**
[33. Build Principles](#33-build-principles) · [34. Work Packages](#34-work-packages)

**Part VIII: Governance**
[35. Licensing](#35-licensing) · [36. Open Decisions and Defaults](#36-open-decisions-and-defaults) · [37. Document Governance](#37-document-governance)

**Appendices**
[A Glossary](#appendix-a-glossary) · [B Claims Reference](#appendix-b-claims-reference) · [C Keycloak Object Inventory](#appendix-c-keycloak-object-inventory) · [D Environment Variables](#appendix-d-environment-variables) · [E Naming Conventions](#appendix-e-naming-conventions) · [F Access Database DDL](#appendix-f-access-database-ddl) · [G Permission Catalog](#appendix-g-permission-catalog-initial) · [H Event Schemas](#appendix-h-event-schemas) · [I API Index](#appendix-i-api-index) · [J AGENTS.md Template](#appendix-j-agentsmd-template)

---

# 0. Agent Operating Manual

This part is for any LLM agent (or new engineer) that will build or change the platform. **Read it fully before writing code.** It is short on purpose.

## 0.1 Mission

Build and maintain **Excellore**: a multi-tenant SaaS suite of first-party applications (**Shell**, **Profile**, **Access**) with one login, tenant-specific roles, permissions, and app access, and strong tenant data isolation. Work through the **work packages in section 34, in order**, unless a human tells you otherwise.

## 0.2 Where to find things

| You need | Go to |
|---|---|
| What the platform is, who owns what | Parts I and IV |
| How tenants, users, and membership work | [Section 6](#6-tenancy-model) |
| How permissions and roles work | [Section 7](#7-authorization-model) |
| How tenant data is isolated | [Section 8](#8-data-isolation) |
| Keycloak objects to create | [Section 10](#10-keycloak-configuration), [Appendix C](#appendix-c-keycloak-object-inventory) |
| Exact names (clients, roles, hosts, events) | [Appendix E](#appendix-e-naming-conventions) |
| Database tables | [Appendix F](#appendix-f-access-database-ddl) |
| Permission keys | [Appendix G](#appendix-g-permission-catalog-initial) |
| What to build next, with acceptance tests | [Section 34](#34-work-packages) |
| What to do when the book is silent | [Section 0.5](#05-ambiguity-protocol) and [section 36](#36-open-decisions-and-defaults) |

## 0.3 Invariants (non-negotiable)

These hold for every change. A change that violates one is rejected, whatever the reason. Fixing a violation is always higher priority than delivering a feature.

| ID | Invariant |
|---|---|
| **INV-01** | **Tokens are tenant-agnostic.** Keycloak tokens identify a person (`sub`) and how they authenticated (`acr`). They MUST NOT carry tenant IDs, tenant roles, or tenant permissions. |
| **INV-02** | **Tenant authorization is resolved server-side** by the Access authority for `(userId, tenantId)`, on every request (cached briefly). It is never taken from the browser, a token claim, or a header alone. |
| **INV-03** | **Every tenant-owned row has `tenant_id` and a PostgreSQL row-level-security (RLS) policy.** Runtime database roles MUST NOT bypass RLS. |
| **INV-04** | **Every tenant-scoped API route contains `{tenantId}`** and passes the tenant gate (tenant active, membership active, permission held) **before** any handler code runs. |
| **INV-05** | **The browser never receives access or refresh tokens.** Only a BFF holds them, server-side. |
| **INV-06** | **One audience per API.** Each API accepts only tokens whose `aud` is its own. |
| **INV-07** | **Workers carry `tenantId` and run in tenant context** with their own machine identity. Messages never contain tokens. |
| **INV-08** | **Only the Access service changes tenant authorization data** (memberships, roles, assignments, app enablement). The permission catalog is **code-first**: apps declare permissions in their repository. |
| **INV-09** | **Apps talk through events.** The only synchronous cross-app calls are the documented internal contracts of the Access service ([7.8](#78-resolve-contract-pdp-and-pep)). |
| **INV-10** | **Fail closed.** Unknown tenant, non-member, suspended tenant, missing permission, or unavailable authorization data means **deny**. A non-member gets `404`, not `403` (no tenant enumeration). |
| **INV-11** | **No secrets, tokens, or personal data in logs, events, URLs, or error messages.** The sole URL exception is Keycloak's time-limited HTTPS execute-actions link for new-user onboarding. Never put an Excellore invitation code in a URL, log or forward the Keycloak action URL, or extend this exception to other flows (RFC-0006). |
| **INV-12** | **Every security-relevant change is audited** with `tenant_id` (or platform scope), actor, target, before and after, and reason where required. |
| **INV-13** | **Identity is global; membership is per tenant.** A person has one Keycloak identity and may be a member of many tenants. No tenant-specific copies of users or passwords. |
| **INV-14** | **Everything is code.** Realm configuration, infrastructure, migrations, pipelines. No manual production changes. |
| **INV-15** | **Standards only.** OIDC, OAuth 2.0, JWT, RFC 9457 problems, OpenTelemetry. No custom token formats and no home-made cryptography. |
| **INV-16** | **No change ships without tests,** including a cross-tenant leakage test for every new tenant-scoped endpoint and table. |
| **INV-17** | **New permissions are default-deny.** A permission reaches a role only through the catalog's `systemRoles` list or an explicit tenant assignment. |
| **INV-18** | **Platform staff are managed as code, and see tenant data only through time-boxed, audited support grants.** No impersonation of users. |

## 0.4 Rules of engagement

**Do**

- Work in small, reviewable changes. One work package step per commit where possible.
- Read the relevant sections and invariants before each task. Quote the invariant IDs your change touches in the commit body (or PR description when a PR is required).
- Generate code from the **templates and shared libraries**. If something security-relevant is missing from a library, add it to the library, not to one app.
- Write the tests first or together with the code. Run the full checks listed in [section 34](#34-work-packages) before declaring a step done.
- Keep names exactly as in [Appendix E](#appendix-e-naming-conventions).
- Record every assumption you make as a proposed ADR in `docs/adr/`.

**Do not**

- Do not put a tenant ID, role, or permission into a Keycloak token, mapper, or claim (INV-01).
- Do not read a tenant from a header, cookie, or body and trust it (INV-02, INV-04).
- Do not add a table without `tenant_id` and an RLS policy unless it is explicitly a platform table ([8.2](#82-table-classes)) (INV-03).
- Do not give application runtime database roles `BYPASSRLS`, superuser, or table ownership (INV-03).
- Do not store or log tokens, passwords, secrets, or full cookies (INV-05, INV-11).
- Do not call another app's API from your app, or read another app's database (INV-09).
- Do not hard-code secrets, hostnames, or tenant data. Use configuration and the secrets manager.
- Do not add a dependency without checking its license against [section 35](#35-licensing). When unsure, stop and ask.
- Do not disable, skip, or weaken a test, a security header, or a check to make a build pass.
- Do not run destructive commands against any shared environment.

## 0.5 Ambiguity protocol

When the book does not answer your question, follow this order:

1. **Search the book** (invariants, section 36 defaults, glossary, appendices).
2. If section 36 gives a **binding default**, use it and move on.
3. If the choice is **low-risk and reversible** (naming of an internal helper, file layout inside a project), pick the simplest option consistent with the book, and note it in the commit body (or PR).
4. If the choice **affects security, tenant isolation, data models, public contracts, or cost**, write a short proposed ADR (template in [37.2](#372-adr-template)), implement the **safest** option, and flag it in the commit body (or PR) for a human decision.
5. If an invariant seems to conflict with a requirement, **stop and ask a human**. Never resolve it silently.

## 0.6 Definition of done (every change)

- [ ] Builds, lints, and type-checks (`pnpm -w build`, `pnpm -w lint`, `dotnet build`).
- [ ] Unit and integration tests pass (`pnpm -w test`, `dotnet test`).
- [ ] Every new tenant-scoped endpoint and table has a cross-tenant leakage test (INV-16).
- [ ] Every new permission is in the catalog file and has an authorization test (granted and denied).
- [ ] Every new event has a schema in `Excellore.Contracts` and a consumer idempotency test.
- [ ] No secret, token, or personal data appears in code, logs, fixtures, or events.
- [ ] Migrations are backward compatible and include RLS for new tenant tables.
- [ ] Audit events are written for security-relevant changes.
- [ ] Docs updated (this book or an ADR) if behavior or a contract changed.

## 0.7 PR and commit conventions

- Direct commits to `main` are the solo-maintainer default (ADR-0017). For a required PR, use `wp-<nn>/<short-description>` or `fix/<short-description>`.
- Commits: Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `chore:`, `refactor:`), scope = project (`feat(access-api): …`).
- Commit body (or PR description): what and why, invariants touched, how verified, ADRs proposed, follow-ups.
- Changes under the security-owned paths in [24.2](#242-branching-and-review) need a recorded security review. While the repository has one maintainer, the author records this in the commit body (or PR when required); no separate approving reviewer is required (ADR-0017).

---

# Part I: Platform Foundations

## 1. Purpose and Scope

### 1.1 What Excellore is

Excellore is a **multi-tenant SaaS platform**: a suite of first-party applications that feel like one product. **Tenants** (customer organizations) each have their own members, roles, permissions, and set of enabled apps. A person signs in once and can work in any tenant they belong to, switching between tenants and apps without signing in again.

### 1.2 Goals

- Single sign-on across all apps, with one global identity per person.
- **Tenant-specific** roles, permissions, and app access, including tenant-defined custom roles.
- **Strong tenant isolation** at every layer: authorization, API, database, cache, storage, messaging, logs.
- The browser never holds tokens (BFF pattern); each API accepts only tokens meant for it.
- Background jobs run in tenant context with their own machine identity.
- New apps are a configuration and template exercise, not a design exercise.
- Open-source stack where practical, standards-based so the IdP can be replaced.

### 1.3 Non-goals

- Authenticating third-party applications (no public OAuth platform).
- Building our own identity provider.
- Micro-frontends. Apps are separate deployables that share look and feel through a design system.
- Billing and invoicing. The platform exposes **plan and entitlement hooks** ([6.6](#66-tenant-app-enablement)); a billing system plugs in later.

### 1.4 Scope of the current release

Three front-end apps (**Shell**, **Profile**, **Access**), two services with APIs (**Profile**, **Access**), their workers, Keycloak, and shared infrastructure.

---

## 2. Principles

| # | Principle | Meaning in practice |
|---|---|---|
| P1 | **One identity, many tenants** | Credentials and authentication live in Keycloak. Tenant membership and authorization live in Access. |
| P2 | **Authentication and authorization are separate systems** | Keycloak answers "who are you, how strongly did you prove it". Access answers "what may you do in tenant T". |
| P3 | **Browser is untrusted** | Tokens stay server-side. The UI reflects authorization; it never decides it. |
| P4 | **Isolation by default** | Tenant isolation is enforced in more than one layer, so a single bug does not leak data. |
| P5 | **Least privilege everywhere** | Per-API audiences, per-worker identities, narrow permissions, narrow network paths. |
| P6 | **Fail closed** | When in doubt, deny. |
| P7 | **Code-first catalog** | Permissions are declared in code next to the feature that needs them. |
| P8 | **Asynchronous between services** | Events and queues, with narrow, documented synchronous exceptions. |
| P9 | **Everything is code** | Realm, infrastructure, schema, pipelines, dashboards. |
| P10 | **Observable and auditable** | Every security-relevant action names the human or machine behind it and the tenant it affected. |
| P11 | **Templates over reinvention** | Security-sensitive code is written once and reused. |
| P12 | **Standards over cleverness** | OIDC, OAuth 2.0, JWT, RFC 9457, OpenTelemetry. |
| P13 | **Open source first, with eyes open** | Check licenses at adoption and on major upgrades. |

---

## 3. Platform Catalog

### 3.1 Applications

| App | Host | Purpose | Tenant-scoped? |
|---|---|---|---|
| **Shell** | `excellore.com` | Front door: sign-in, tenant picker and switcher, per-tenant app launcher, global sign-out | Both (picker is user-level; launcher is tenant-level) |
| **Profile** | `profile.excellore.com` | My account (global): details, avatar, preferences, security, sessions, data export, deletion. Plus **member profile** inside a tenant | Both |
| **Access** | `access.excellore.com` | **Tenant console**: members, invitations, roles, assignments, groups, audit, tenant settings. **Platform console**: tenants, app enablement, support grants. Also the platform's **authorization authority** | Both |

### 3.2 Services and components

| Component | Shell | Profile | Access |
|---|---|---|---|
| Front end + BFF | `excellore-shell` app | `excellore-profile` app | `excellore-access` app |
| OIDC client (BFF) | `excellore-shell` | `excellore-profile` | `excellore-access` |
| API | none | `Excellore.Profile.Api` | `Excellore.Access.Api` |
| API audience | none | `api-profile` | `api-access` |
| Worker | none | `Excellore.Profile.Worker` (`worker-profile`) | `Excellore.Access.Worker` (`worker-access`) |
| Authz machine client | `svc-shell-authz` | `svc-profile-authz` | none (resolves locally) |
| Database | none | `profile` | `access` |
| Session key prefix | `shell:sess:` | `profile:sess:` | `access:sess:` |
| Permission namespace | none | `profile.*` | `access.*` |

### 3.3 Dependency rules

- Apps MUST NOT call each other's APIs or read each other's databases (INV-09).
- **Allowed synchronous exception:** BFFs and APIs of any app call the **internal contracts of the Access service** to resolve a user's tenants and permissions ([7.8](#78-resolve-contract-pdp-and-pep)). Access is the leaf authority; it calls no other app.
- Only **Access** changes tenant authorization data (INV-08). Only **Profile** changes a person's own profile data.
- Cross-app reactions (cleanup, notifications, cache invalidation) use **events** ([section 16](#16-platform-events-and-inter-app-communication)).

---

## 4. Architecture Overview

```
                      ┌────────────────────────────────────────────────────┐
                      │ Keycloak (auth.excellore.com)  realm: excellore    │
                      │ Global identities · MFA/passkeys · SSO · tokens    │
                      │ Tokens are TENANT-AGNOSTIC (INV-01)                │
                      └─▲───────▲────────▲────────▲──────────▲─────────────┘
         OIDC code+PKCE │       │        │        │          │ client credentials
                        │       │        │        │          │
   ┌────────────┐  ┌────┴─────┐ ┌┴─────────┐   workers   service identities (svc-*)
   │ Shell      │  │ Profile  │ │ Access   │
   │ UI + BFF   │  │ UI + BFF │ │ UI + BFF │      browser ⇄ BFF: cookie only
   └─────┬──────┘  └────┬─────┘ └────┬─────┘
         │ internal      │ Bearer     │ Bearer
         │ context       │ aud=       │ aud=
         │               │ api-profile│ api-access
         │          ┌────▼─────┐ ┌────▼──────────────────────────┐
         │          │ Profile  │ │ Access API  = AUTHORIZATION   │
         │          │ API      ├─► AUTHORITY (PDP)               │
         └──────────►  (PEP)   │ │ tenants · members · roles ·   │
          resolve   └────┬─────┘ │ assignments · app enablement  │
                        │       └────┬──────────────────────────┘
                  ┌─────▼────┐  ┌────▼─────┐      Shared: Valkey (BFF sessions,
                  │ Postgres │  │ Postgres │      authz cache), RabbitMQ
                  │ profile  │  │ access   │      (excellore.events), object
                  │ RLS      │  │ RLS      │      storage, OTel stack
                  └──────────┘  └──────────┘
```

- **PDP** = Policy Decision Point (Access authority). **PEP** = Policy Enforcement Point (each API, through `Excellore.Authz`).
- Tenant context travels as `{tenantId}` in API routes (INV-04) and as `tenantId` in messages. It never travels in tokens (INV-01).

### 4.1 Component responsibilities

| Component | Responsibility | Must NOT |
|---|---|---|
| Keycloak | Authenticate people, hold the SSO session, issue tenant-agnostic tokens, MFA, credentials | Know about tenants, tenant roles, or permissions |
| BFF | Run OIDC, hold tokens server-side, issue session cookie, resolve tenant from URL slug, attach tokens, CSRF defense | Contain business logic; expose tokens |
| API (PEP) | Validate tokens, run the tenant gate, enforce permissions, own business rules and data | Redirect to login; hold sessions; trust headers for tenant |
| Access (PDP) | Own tenants, memberships, roles, assignments, app enablement, permission catalog; answer resolve requests | Contain other apps' business logic |
| Worker | Perform background jobs in tenant context with a machine identity | Use or store user tokens |
| Broker | Carry commands and events | Carry tokens or secrets |

---

## 5. Decision Register

Final until changed through [section 37](#37-document-governance).

| ID | Decision | Choice | Rationale |
|---|---|---|---|
| D1 | Protocol | OIDC over OAuth 2.0 | Portable standard |
| D2 | IdP | Keycloak, self-hosted, **single realm** `excellore` | Mature, open source; one global identity per person |
| D3 | User flow | Authorization Code + PKCE (S256), confidential client | Best practice |
| D4 | Browser tokens | BFF, tokens server-side, opaque cookie | Removes token theft via XSS |
| D5 | BFF cardinality | One BFF per front-end app | Isolation |
| D6 | BFF implementation | Next.js App Router server side, in the app project | One deployable per app |
| D7 | API role | Resource servers (PEPs) validating JWTs and enforcing permissions | Clear split |
| D8 | Audience | One audience per API | Confused-deputy defense |
| D9 | Worker identity | Client credentials, one client per worker | No user tokens in queues |
| D10 | Backend | .NET (ASP.NET Core) | Team expertise |
| D11 | Session store | Valkey | Server-side sessions, revocation |
| D12 | App relationship | Separate apps | Simplicity |
| D13 | **Tenancy model** | **Global identity + per-tenant membership** | A person can belong to many tenants |
| D14 | **Where tenant authorization lives** | **Access database (PDP), not Keycloak** | Tenant-defined roles are dynamic; tokens stay small and tenant-agnostic |
| D15 | **Tenant resolution** | **Slug in front-end URL path `/t/{slug}`; `tenantId` in API route** | Bookmarkable, multi-tab, explicit, easy to enforce |
| D16 | **Data isolation** | **Pooled PostgreSQL (shared schema) with `tenant_id` + RLS**, silo option later | Cost-efficient, safe with defense in depth |
| D17 | **Authorization model** | **RBAC with code-first permission catalog; additive only (no deny rules)** | Simple to reason about and test |
| D18 | **App access** | **Two gates: tenant app enablement (platform-controlled) and the `<app>.app.access` permission (tenant-controlled)** | Plans gate apps; tenants gate people |
| D19 | **Platform staff** | **Keycloak realm roles managed as code; tenant data only via support grants** | Strong separation of duties |
| D20 | Inter-app communication | Events on RabbitMQ topic exchange, plus Access internal contracts | Loose coupling |
| D21 | Step-up auth | Required for privileged operations | Limits damage from a hijacked session |
| D22 | Database | PostgreSQL, one database per service | Ownership and blast radius |
| D23 | Permission delivery | **Resolve per request with short cache and event invalidation; never in tokens** | Always-fresh, no token bloat |
| D24 | Repository review while solo | **Owner review record and required CI on every PR; no separate approving reviewer until another maintainer joins** | Keeps checks and an audit trail while the only code owner cannot approve their own PR (ADR-0003) |

---

# Part II: Multi-Tenancy

## 6. Tenancy Model

### 6.1 Core concepts

| Concept | Definition | Lives in |
|---|---|---|
| **Platform** | Excellore itself. Operated by platform staff. | n/a |
| **Tenant** | A customer organization. The unit of isolation, billing, and administration. | Access DB, table `tenants` |
| **Identity (user)** | A person's global account: login, credentials, MFA. One per person. | Keycloak (source of truth), mirrored in Access DB `identities` |
| **Membership** | The link between an identity and a tenant, with a status. | Access DB, `tenant_members` |
| **Role** | A named set of permissions. **System roles** are templates available to every tenant. **Custom roles** belong to one tenant. | Access DB, `roles` |
| **Permission** | A capability key `<app>.<resource>.<action>`, declared in code. | Catalog (code), Access DB `permissions` |
| **Assignment** | A role granted to a member (directly or through a group) in one tenant. | Access DB |
| **App enablement** | Which apps a tenant may use (plan-controlled). | Access DB, `tenant_apps` |
| **Group** | Optional tenant-scoped set of members that receives roles together. | Access DB |
| **Support grant** | A time-boxed permission for platform staff to access one tenant. | Access DB |

### 6.2 Relationship diagram

```
 Identity ──< Membership >── Tenant ──< TenantApp >── App
                │                │
                │                └──< Role (custom) >──< RolePermission >── Permission
                │
                └──< RoleAssignment >── Role (system or custom)
                └──< GroupMember >── Group ──< GroupRoleAssignment >── Role
```

### 6.3 Tenant attributes

| Attribute | Rules |
|---|---|
| `id` | UUID (v7 preferred). Immutable. Used in API routes, messages, and RLS. |
| `slug` | Lowercase `[a-z0-9-]`, 3 to 40 chars, unique, no leading or trailing hyphen, not in the reserved list. Used in front-end URLs. May be renamed (old slug MAY redirect for 30 days). |
| `name` | Display name. |
| `status` | See lifecycle below. |
| `plan` | Opaque key used for entitlements (for example `starter`, `business`). |
| `region` | Reserved for data residency ([36](#36-open-decisions-and-defaults) Q-05). |
| `created_at`, `updated_at`, `deleted_at` | Timestamps. |

**Reserved slugs:** `www api auth admin platform t static assets support status help login logout app apps internal profile access shell excellore`.

### 6.4 Tenant lifecycle

```
provisioning ──► active ◄──► suspended
                   │
                   └──► pending_deletion ──► deleted
```

| State | Effect |
|---|---|
| `provisioning` | Being created (owner invited, defaults seeded). Not usable. |
| `active` | Normal operation. |
| `suspended` | All members denied (resolve returns deny). Data retained. Used for non-payment or abuse. Reversible by platform operators. |
| `pending_deletion` | Owner or platform requested deletion. All members denied. Grace period (default **14 days**, Q-06) during which it can be restored. |
| `deleted` | Erasure complete. Row kept as a tombstone (id, slug reserved, deleted_at) for audit. |

Deletion publishes `tenant.deletion.requested.v1` first, then `tenant.deleted.v1` after the grace period. Every service erases the tenant's data on `tenant.deleted.v1` and emits a confirmation (see [22.3](#223-tenant-and-user-deletion)).

### 6.5 Membership

| Status | Meaning |
|---|---|
| `invited` | Invitation sent, not yet accepted. No access. |
| `active` | Member. Authorization resolves. |
| `suspended` | Temporarily blocked by a tenant admin. No access. |
| `removed` | Left or was removed. Row kept for audit; no access. |

Rules:

- A tenant MUST always have at least one **active member holding the `owner` system role** (last-owner protection).
- A person can be a member of **many tenants**. Their identity, password, and MFA are shared; their roles are not.
- Removing a member removes their access to that tenant only. The global identity is untouched.
- **Invitation security:** the one-time code contains an opaque tenant ID and a random 256-bit secret, stored only as a hash, single-use, and expires (default 7 days). The invitation link contains only an opaque invitation ID; the invitee enters the code separately. Acceptance requires the signed-in identity to have a **verified email equal to the invited email**. Responses to invitation requests are uniform so nobody can learn whether an email already has an account.

### 6.6 Tenant app enablement

- **Core apps** (Shell, Profile, Access) are always enabled for every active tenant.
- **Other apps** are enabled per tenant by platform operators (or by plan rules), stored in `tenant_apps` with `status` (`enabled`, `disabled`) and optional `features` JSON (limits such as `maxMembers`, `maxCustomRoles`).
- App enablement is the **first gate**. A permission of a disabled app is never effective, even if a role contains it ([7.5](#75-effective-permission-algorithm)).
- Billing integration: a future billing system updates `tenant_apps` and `plan` through the platform API. Nothing else changes.

### 6.7 Tenant resolution and URLs

| Surface | Pattern | Example |
|---|---|---|
| Front end, tenant-scoped | `https://<app>.excellore.com/t/{slug}/…` | `https://access.excellore.com/t/acme/members` |
| Front end, user-level (no tenant) | `https://<app>.excellore.com/…` | `https://profile.excellore.com/account` |
| Shell | `/` (picker or redirect), `/t/{slug}` (launcher) | `https://excellore.com/t/acme` |
| API, tenant-scoped | `/v1/tenants/{tenantId}/…` | `/v1/tenants/0192…/members` |
| API, user-level | `/v1/me/…` | `/v1/me/profile` |
| API, platform | `/v1/platform/…` | `/v1/platform/tenants` |
| API, internal (Access only) | `/internal/v1/…` | `/internal/v1/authz/resolve` |

Resolution steps (BFF):

1. Extract `{slug}` from the URL.
2. Resolve slug → tenant and verify membership through the Access internal contract (cached for up to 60 s per session and slug).
3. If the user is not an active member: respond `404` (INV-10). Never reveal that the tenant exists.
4. Call the app's API with `/v1/tenants/{tenantId}/…` and the user's access token.
5. The API repeats the tenant gate independently (INV-04). The BFF's check is for UX only.

Switching tenants is navigating to another `/t/{slug}`. Different browser tabs MAY be in different tenants at the same time.

**Last used tenant:** the Shell remembers it in a non-sensitive cookie (`excellore-last-tenant`, slug only) to redirect `/` to the user's last tenant. If the user is a member of exactly one tenant, `/` redirects there.

### 6.8 Tenant context propagation

| Channel | How tenant travels | Verified by |
|---|---|---|
| Browser → BFF | URL `/t/{slug}` | BFF (slug → id, membership) |
| BFF → API | Route `{tenantId}` | API tenant gate |
| API → database | `set_config('app.tenant_id', …, true)` in the request transaction | RLS |
| API → broker | `tenantId` in envelope | Consumer sets tenant context |
| Worker → database | Same as API, per job | RLS |
| Logs, traces, metrics | `tenant.id` attribute (an opaque ID, not personal data) | n/a |
| Cache keys | `…:t:{tenantId}:…` | n/a |
| Object storage keys | `tenants/{tenantId}/…` | n/a |

### 6.9 Two administration levels

| Level | Who | Where | How roles are managed |
|---|---|---|---|
| **Tenant** | Tenant owners, admins, and holders of custom roles | Access app, `/t/{slug}/…` | Tenant roles and permissions in the Access database (section 7) |
| **Platform** | Excellore staff | Access app, `/platform/…` (separate route group, separate API policies, separate audit scope) | **Keycloak realm roles managed as code** ([10.4](#104-roles-platform-and-machine)) |

Platform staff have **no standing access** to tenant data (INV-18). They get it through **support grants**: a tenant owner (or break-glass with dual approval and alerting) creates a time-boxed grant (default max 8 hours, read-only by default) that gives a named staff member a defined role in that tenant. Every action under a grant is audited with the grant ID.

---

## 7. Authorization Model

### 7.1 Overview

```
Request ─► BFF (session, slug→tenant) ─► API (JWT valid? aud ok?)
                                           │
                                           ▼
                                 Tenant gate (INV-04)
                           resolve(userId, tenantId) via PDP
                      tenant active? member active? permission held?
                                           │ allow
                                           ▼
                              handler (+ resource-level checks)
```

Keycloak proves **who**. Access decides **what, in which tenant**.

### 7.2 Permissions

- A permission is a string key: `<app>.<resource>.<action>` (lowercase, dot-separated). Examples: `access.role.manage`, `profile.member.read`.
- Permissions are **declared in code** in each service's `permissions.yaml` ([17.2](#172-the-permission-catalog-file), [Appendix G](#appendix-g-permission-catalog-initial)) and synchronized into the Access database on deploy (INV-08).
- Attributes per permission: `key`, `app`, `description`, `privileged` (changes need step-up), `ownerOnly` (cannot be put in custom roles), `systemRoles` (default membership in system roles), `deprecated`.
- **App access permission:** every app has `<app>.app.access`. Holding it (and the app being enabled for the tenant) is what lets a member open the app inside the tenant.
- Permissions are **additive**. There are no deny rules (D17).

### 7.3 Roles

| Kind | Scope | Editable | Examples |
|---|---|---|---|
| **System role** | Global template, visible in every tenant | By platform deploy only (via catalog `systemRoles`) | `owner`, `admin`, `member`, `viewer`, `auditor` |
| **Custom role** | One tenant | By tenant members holding `access.role.manage` | `billing-manager`, `support-agent` |

System roles:

| Role | Intent |
|---|---|
| `owner` | Full control of the tenant, including settings, deletion, and ownership transfer. At least one per tenant. |
| `admin` | Manages members, roles, and day-to-day administration. Cannot do owner-only actions. |
| `member` | Standard user: opens enabled apps, edits own member profile, requests access. |
| `viewer` | Read-only access to enabled apps. |
| `auditor` | Read access to audit logs and membership information. |

Each permission lists in the catalog which system roles include it by default, so a **new app contributes to system roles automatically** while remaining default-deny for anyone else (INV-17).

Custom role rules:

- Contains only non-`ownerOnly`, non-deprecated permissions of **apps enabled for the tenant**.
- Name unique within the tenant (case-insensitive), 3 to 60 chars.
- Limit per tenant from plan features (default 25).
- Deleting a role in use is refused until it is unassigned (or reassigned in the same operation).
- Role changes take effect through cache invalidation within seconds ([7.9](#79-caching-and-invalidation)).

### 7.4 Assignments and groups

- A **role assignment** grants one role to one member in one tenant, with optional `expires_at` (time-bound access) and a `reason`.
- Grant and revoke requests require a bounded machine-safe reason code. The ordinary assignment endpoints do not change the `owner` role; ownership moves through the accepted transfer flow. An expired direct grant may be replaced atomically with a new grant.
- A **group** is a tenant-scoped set of members. A **group role assignment** gives a role to every member of the group.
- Group member changes use a user ID in the request body, never the URL. Adding a member rechecks every effective group role for escalation and step-up. Group role changes cannot alter the actor's own roles, and the ordinary group API does not grant or revoke the `owner` role. Group member and group role changes require machine-safe reason codes.
- A member's roles = direct assignments ∪ roles via groups, ignoring expired entries.
- Groups are part of the data model from the start. Their UI MAY ship later (WP-08b).

### 7.5 Effective permission algorithm

```
resolve(userId, tenantId):
  tenant  = tenants[tenantId]            ; deny if missing or status != 'active'
  member  = tenant_members[tenantId,userId] ; deny if missing or status != 'active'
  roles   = direct assignments ∪ group role assignments       (not expired)
  perms   = ∪ permissions of roles
  perms   = perms − permissions marked deprecated-and-removed
  enabled = apps enabled for tenant  (core apps always included)
  perms   = { p ∈ perms : app(p) ∈ enabled }
  return { tenant, membership, roles: names(roles), permissions: perms,
           apps: enabled ∩ { a : a.app.access ∈ perms }, version }
```

Rules:

- Deny by default. No match means no permission.
- `owner` receives every permission of every enabled app that is flagged for `owner` in the catalog, which in practice is all non-platform permissions.
- Resource-level rules (for example "only the creator may delete") live in handler code **after** the permission check.

### 7.6 Platform roles

Realm roles in Keycloak, assigned **only through code** (`infra/keycloak/platform-staff.yaml`, applied by pipeline; the pull request is the four-eyes approval).

| Role | May |
|---|---|
| `platform-admin` | Everything below, plus Keycloak and infrastructure administration. Break-glass. Named individuals only |
| `platform-operator` | Create, suspend, reactivate, delete tenants; enable or disable apps; set plans |
| `platform-support` | Request and use support grants; read tenant metadata (not tenant data) |
| `platform-auditor` | Read platform-level audit; read tenant audit only under a support grant |
| `platform-security` | Read security events, force-end sessions, suspend tenants during incidents |

Platform roles appear in the `platform_roles` claim of tokens issued for the Access API audience only ([10.5](#105-client-scopes-and-mappers)).

### 7.7 Privileged operations and anti-escalation

A permission or role is **privileged** when it carries a `privileged: true` permission. Rules (all MUST be enforced in the Access API and covered by tests):

| # | Rule |
|---|---|
| S1 | **No escalation.** An actor may only assign a role whose permissions are all held by the actor in that tenant. |
| S2 | **No self-assignment.** An actor cannot change their own roles or groups, or approve their own requests. |
| S3 | **Step-up.** Assigning, creating, or editing a privileged role, changing tenant settings, transferring ownership, starting deletion, and creating support grants require a recent MFA (`acr` at the required level, verified by the API from the token). |
| S4 | **Last owner.** A change that would leave a tenant with no active owner is refused. |
| S5 | **Tenant boundary.** An actor can never read or change data of a tenant where they have no active membership (except under a support grant). |
| S6 | **Complete audit.** Every allowed and denied sensitive operation is audited with `tenant_id`. |
| S7 | **Rate limits.** Bulk and sensitive operations are rate-limited per user and per tenant. |
| S8 | **Role content limits.** Custom roles may not contain `ownerOnly` permissions or permissions of apps not enabled for the tenant. |
| S9 | **Ownership transfer** needs step-up by the current owner and acceptance by the new owner. |
| S10 | **Service containment.** `svc-access-admin` is used only by the Access service, and every call is tied to a human `sub` in the audit event. |
| S11 | **Invitation integrity.** See [6.5](#65-membership). |
| S12 | **Support grants** are time-boxed, scoped, audited, and visible to the tenant owner. |

### 7.8 Resolve contract (PDP and PEP)

The Access API exposes **internal** endpoints (not routed through the public ingress; reachable only on the private network, protected by a machine token with the `access.authz.resolve` role):

```
POST /internal/v1/authz/resolve
Authorization: Bearer <machine token, aud=api-access, role access.authz.resolve>
{ "userId": "…", "tenantId": "…" }

200 OK
{
  "tenantId": "…",
  "tenantStatus": "active",
  "membershipStatus": "active",
  "roles": ["admin"],
  "permissions": ["access.member.read", "profile.app.access", "…"],
  "apps": ["shell", "profile", "access"],
  "version": 42,
  "resolvedAt": "2026-01-01T12:00:00Z"
}
ETag: "42"
```

Denied cases return `200` with `"decision": "deny"` and an empty permission list (so callers cache denials briefly and never see differing error shapes). Unknown tenant or user is a deny, not an error.

```
POST /internal/v1/users/tenants:list
{ "userId": "…" }
→ [{ "tenantId", "slug", "name", "status", "roles": [...], "apps": [...] }]   # active memberships only

POST /internal/v1/tenants:resolve-slug
{ "userId": "…", "slug": "acme" }
→ { "tenantId", "slug", "name" } or 404 if not an active member
```

User IDs are sent in request bodies, never in URLs (INV-11). These endpoints use the same machine-token protection as the authorization resolver.

**PEP in every API** uses the shared library `Excellore.Authz`:

- Endpoint metadata `RequirePermission("access.member.read")` and the route value `{tenantId}`.
- A **tenant gate** filter runs after JWT validation and before the handler: it takes `sub` from the token, `tenantId` from the route, calls the cached resolver, checks the permission, then **opens the tenant context** (RLS setting, logging scope) for the request.
- If the resolver cannot reach Access: **writes fail closed** (`503`), **reads** MAY use a cached snapshot up to a hard maximum age of **5 minutes** (Q-07). Never serve a denied-then-allowed flip from stale data beyond that.

The **BFF** uses the same contract (through its app's `svc-*-authz` identity) to convert slugs and drive UI visibility. UI hints are never security.

### 7.9 Caching and invalidation

| Cache | Key | TTL | Invalidated by |
|---|---|---|---|
| PEP snapshot | `authz:{tenantId}:{userId}` (Valkey, with a short in-memory layer) | 60 s | `access.authz.changed.v1` event `{tenantId, userIds? }`; absent `userIds` means the whole tenant |
| BFF context | `ctx:{sid}:{slug}` | Up to 60 s (fresh resolution is allowed) | Session end |
| Slug → id | `slug:{slug}` | 5 min | `tenant.updated.v1` via tenant-ID reverse index, or clear the slug cache namespace |

Access increments a per-tenant `authz_version` on every change that can affect permissions (role, assignment, group, membership, tenant status, app enablement) and publishes `access.authz.changed.v1` through the outbox. Consumers drop matching cache entries. The 60 s TTL is the backstop if an event is delayed.

### 7.10 Worker authorization

Workers are machine identities. They do **not** run the user's permission check; instead:

- The API that publishes the job checks the user's permission first.
- The worker re-validates ownership of the referenced data **within the tenant context** (RLS guarantees it cannot read other tenants).
- Jobs triggered by the system (schedules) iterate tenants and set tenant context for each.

---

## 8. Data Isolation

### 8.1 Strategy

**Pooled** PostgreSQL: all tenants share a database and schema; every tenant-owned row carries `tenant_id`; **row-level security** enforces isolation in the database itself, in addition to application filters (defense in depth, INV-03). A **silo** option (dedicated database per large or regulated tenant) is reserved behind a `ITenantDatabaseResolver` abstraction (Q-08) and is not built in the first release.

### 8.2 Table classes

| Class | Definition | `tenant_id` | RLS | Examples |
|---|---|---|---|---|
| **T (tenant-owned)** | Belongs to one tenant | Required, `NOT NULL` | **Required**, `FORCE` | `tenant_members`, `role_assignments`, `audit_events`, `tenant_member_profiles` |
| **P (platform)** | Global data not owned by a tenant | None (see exemptions below) | Not applied; access limited by API policy and DB grants | `tenants`, `apps`, `permissions`, `identities`, `user_profiles` |
| **H (hybrid)** | System rows (`tenant_id IS NULL`) plus tenant rows | Nullable | Required with a hybrid policy | `roles` |

Every new table MUST be classified in its migration (comment) and in the migration review.

**Exemptions.** A small number of platform tables carry a `tenant_id` column as a key or routing field but are not tenant-isolated by RLS (for example `user_tenant_index`, `support_grants`, `outbox`). They MUST be listed in `infra/db/rls-exempt.txt`, which is owned by the security owner (`CODEOWNERS`), and are protected by API policy and database grants instead. The RLS meta-test ([25.2](#252-tenant-isolation-test-harness)) skips exactly the listed tables. Adding to the list requires security review.

### 8.3 RLS pattern

```sql
-- Table class T
ALTER TABLE tenant_members ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_members FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON tenant_members
  USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

-- Table class H (roles): system rows readable by all, writable never through the app role
CREATE POLICY roles_read ON roles FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY roles_write ON roles FOR ALL
  USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```

Facts the agent MUST respect:

- If `app.tenant_id` is unset, `NULLIF(…, '')` yields `NULL`, the comparison is not true, and **no rows are visible**. This is the fail-closed behavior we rely on.
- **Superusers, roles with `BYPASSRLS`, and table owners (unless `FORCE`) bypass RLS.** Therefore:
  - Migrations run as an **owner role** (`<app>_migrator`).
  - The application runs as a **different, non-owner role** (`<app>_app`) with only `SELECT/INSERT/UPDATE/DELETE` on the tables it needs, and no `BYPASSRLS`.
  - Tables use `FORCE ROW LEVEL SECURITY`.
- Set the tenant with a **transaction-local** setting: `SELECT set_config('app.tenant_id', $1, true)`. Never use a session-level setting; pooled connections would leak tenant context between requests.
- A second setting, `app.scope`, MAY be set to `platform` (also transaction-local) **only** by handlers behind the `Platform.*` policies and by catalog sync. It exists solely so those paths can read or write rows whose `tenant_id IS NULL` (system roles, platform-scope audit). It never grants access to rows of any tenant. Tenant-scoped code paths never set it.

### 8.4 Application pattern (.NET, EF Core)

- `Excellore.Platform` provides `ITenantContext` (current `TenantId`), an ASP.NET filter and a worker scope that set it, and a **unit of work that opens a transaction per request or job** and calls `set_config('app.tenant_id', …, true)` as its first statement.
- An EF Core **global query filter** (`entity.TenantId == tenantContext.TenantId`) is applied to every class T entity as a second line of defense.
- `SaveChanges` stamps `TenantId` from the context and **throws** if an entity's `TenantId` differs from the context.
- Code that needs a table of class P uses a dedicated `PlatformDbContext`, kept separate so tenant code cannot casually query platform tables.
- **Cross-tenant queries are forbidden** in tenant code. Platform console features operate on class P tables only. Any per-tenant data access by staff happens through a support grant, in that tenant's context.
- Background jobs that must visit every tenant iterate the tenant list (class P) and process each tenant in its own context and transaction.

### 8.5 Other data stores

| Store | Isolation rule |
|---|---|
| Valkey | Keys for tenant data include `t:{tenantId}`. Never cache tenant data under a user-only key. Separate key prefix per app. |
| Object storage | Keys under `tenants/{tenantId}/…`; user-level files under `users/{userId}/…`. Signed URLs scoped to one key. |
| RabbitMQ | Envelope carries `tenantId`; consumers set tenant context before touching data. Queues are per service, not per tenant (first release). |
| Search or analytics (future) | Must carry `tenant_id` and enforce it at query time. |
| Logs and traces | `tenant.id` attribute on every record in tenant context. Per-tenant log access is a platform-security function. |
| Backups | Contain all tenants. Treated at the highest data class. See [section 31](#31-backup-and-disaster-recovery). |

### 8.6 Noisy neighbor controls

- Rate limits per user **and** per tenant at the BFF and API.
- Per-tenant quotas from plan features (members, custom roles, storage, export frequency).
- Per-tenant concurrency limits for expensive jobs (exports, bulk changes).
- Metrics and alerts per tenant ([section 28](#28-observability-and-slos)).

### 8.7 Isolation test requirements

See [25.2](#252-tenant-isolation-test-harness). In short: for every tenant-scoped endpoint, a user from tenant B calling with tenant A's IDs MUST get `404`/`403`; for every class T table, the RLS test suite proves that a connection with tenant B's context sees zero tenant A rows and cannot insert or update them.

---
# Part III: Identity Foundation

## 9. Domains, Hosts, and Cookies

### 9.1 Host layout

| Host | Purpose | Exposure |
|---|---|---|
| `excellore.com` | Shell | Public |
| `profile.excellore.com` | Profile UI + BFF | Public |
| `access.excellore.com` | Access UI + BFF | Public |
| `auth.excellore.com` | Keycloak (login, token, end-session, discovery) | Public (selected paths only) |
| `admin-auth.excellore.internal` | Keycloak admin console | Internal / VPN only |
| `api-profile.internal.excellore.com` | Profile API | Private network |
| `api-access.internal.excellore.com` | Access API (public contracts `/v1/**` for BFFs; `/internal/**` for service callers) | Private network |

Tenants do **not** get hostnames in the first release. Tenant identity is the `/t/{slug}` path segment (D15). Custom domains per tenant are a future option (Q-10).

### 9.2 Rules

- One registrable domain so `SameSite` cookies behave predictably.
- Session cookies are **host-only**; never `Domain=.excellore.com`. Name prefix `__Host-` (example `__Host-excellore-profile`).
- Flags: `HttpOnly; Secure; SameSite=Lax` (`Strict` where flows allow).
- The cookie holds only an opaque random session ID (256 bits). Tokens never go in the cookie.
- The `excellore-last-tenant` cookie holds a slug only, is not security-relevant, and is `Secure; SameSite=Lax`.
- APIs are not publicly reachable. `/internal/**` is additionally blocked at the ingress and reachable only from the private network.
- The Keycloak admin console and the `master` realm MUST NOT be reachable from the internet.

---

## 10. Keycloak Configuration

### 10.1 Realm and tenancy stance

- **One realm**, `excellore`, holds every identity (D2, INV-13). Do **not** create a realm per tenant: it breaks SSO across tenants and does not scale.
- Keycloak knows nothing about tenants (INV-01). Tenant membership, roles, and app access are in the Access database.
- *Future option:* Keycloak **Organizations** MAY be used later only for **per-tenant enterprise login** (a tenant's own SAML or OIDC IdP, home-realm discovery by email domain) **(verify feature level)**. Even then, memberships stay in Access (Q-09).
- Issuer: `https://auth.excellore.com/realms/excellore`. Discovery: `…/.well-known/openid-configuration`.
- Configure hostname and proxy settings so the issuer is **identical** for all consumers.

### 10.2 Realm settings

| Setting | Value | Purpose |
|---|---|---|
| Access token lifespan | 5 minutes | Limits leaked-token damage |
| SSO Session Idle / Max | 30 minutes / 8 to 10 hours | Idle timeout and forced re-login |
| Revoke Refresh Token | On (rotation), reuse 0 | Detect stolen refresh tokens |
| Brute-force detection | On | Lockout |
| Password policy | Length 12+, not username/email, blacklist, history | Hygiene |
| Email verification | On | Verified identities (needed for invitation matching) |
| Required actions at first login | `VERIFY_EMAIL`, `UPDATE_PASSWORD`, `CONFIGURE_TOTP` or WebAuthn | MFA enrollment |
| Self-registration | **Off** (default; Q-01) | Users enter by invitation |
| Declarative user profile | On, attributes per [10.6](#106-user-profile-attributes) | Controlled attributes |
| Events | Login and admin events on, stored in Keycloak; log export deferred until a redacting listener is available (ADR-0004) | Audit without personal data in logs (INV-11) |

### 10.3 Clients

#### BFF clients (confidential, user login)

| Client ID | Used by | Standard flow | Direct grants | Service accounts | PKCE |
|---|---|---|---|---|---|
| `excellore-shell` | Shell BFF | On | Off | Off | S256 required |
| `excellore-profile` | Profile BFF | On | Off | Off | S256 required |
| `excellore-access` | Access BFF | On | Off | Off | S256 required |

Per client: exact redirect URIs (for example `https://profile.excellore.com/auth/callback`), exact post-logout URIs, no web origins, back-channel logout URL (`…/auth/backchannel-logout`) with session required, implicit flow off, consent off, **Full scope allowed off**, default scopes `openid profile email` plus the app's API scope (Shell has none).

Because redirect URIs are exact and tenants live in the path (`/t/{slug}`), the **post-login return URL** is carried in app state (`returnTo`, validated against an allow-list), not in the redirect URI.

#### Resource server clients (audience holders)

| Client ID | Represents | Standard flow | Service accounts |
|---|---|---|---|
| `api-profile` | Profile API | Off | Off |
| `api-access` | Access API | Off | Off |

#### Worker clients

| Client ID | Used by | Service accounts | Standard flow |
|---|---|---|---|
| `worker-profile` | Profile Worker | On | Off |
| `worker-access` | Access Worker | On | Off |

#### Service identities (machine, server-side only)

| Client ID | Used by | Purpose | Roles |
|---|---|---|---|
| `svc-shell-authz` | Shell BFF | Call Access internal contracts | `api-access`: `access.authz.resolve` |
| `svc-profile-authz` | Profile BFF and API | Call Access internal contracts | `api-access`: `access.authz.resolve` |
| `svc-catalog-sync` | CI/CD pipeline | Publish permission catalogs | `api-access`: `access.catalog.sync` |
| `svc-access-admin` | Access API and Worker | Keycloak Admin REST: create users, send required-action emails, disable and enable users | realm-management: `query-users`, `view-users`, `manage-users` (nothing about roles or groups) |
| `svc-profile-identity` | Profile API | Update **own** first name, last name, locale | realm-management: `view-users`, `manage-users` |

Rules: never reachable from browsers; secrets rotated at least every 90 days; prefer fine-grained admin permissions to narrow `manage-users` **(verify)**; every Admin API call is logged with the acting human `sub` from the originating request. New apps add `svc-<app>-authz` ([17](#17-onboarding-a-new-app)).

### 10.4 Roles (platform and machine)

Roles in Keycloak are deliberately few. **Tenant roles are not here** (D14).

| Role | Kind | Meaning | Assigned |
|---|---|---|---|
| `platform-admin` | Realm | Break-glass administrator | By code, named individuals |
| `platform-operator` | Realm | Tenant lifecycle and app enablement | By code |
| `platform-support` | Realm | Support grants, tenant metadata | By code |
| `platform-auditor` | Realm | Platform audit | By code |
| `platform-security` | Realm | Security response | By code |
| `access.authz.resolve` | Client role on `api-access` | May call authz and context internal contracts | Service identities only |
| `access.catalog.sync` | Client role on `api-access` | May publish permission catalogs | `svc-catalog-sync` |
| `access.worker` | Client role on `api-access` | Machine role for Access worker | `worker-access` |
| `profile.worker` | Client role on `api-profile` | Machine role for Profile worker | `worker-profile` |

Platform staff assignment is managed in `infra/keycloak/platform-staff.yaml` and applied by the pipeline (INV-18). Humans never receive `access.*` or `profile.*` client roles; their capabilities come from tenant permissions.

### 10.5 Client scopes and mappers

| Client scope | Mappers | Assigned to |
|---|---|---|
| `api-profile` | Audience `api-profile`; client-role mapper (client `api-profile`) to claim `roles` | `excellore-profile`, `worker-profile` |
| `api-access` | Audience `api-access`; client-role mapper (client `api-access`) to claim `roles`; **realm-role mapper limited to `platform-*`** to claim `platform_roles` | `excellore-access`, `worker-access`, `svc-shell-authz`, `svc-profile-authz`, `svc-catalog-sync` |

Resulting tokens:

- **User token for the Profile BFF:** `aud=api-profile`, `sub`, `acr`, `sid`, standard identity claims. No roles, no tenant.
- **User token for the Access BFF:** `aud=api-access`, plus `platform_roles` (empty for almost everyone).
- **Machine token:** `aud=api-access`, `roles=["access.authz.resolve"]`.

Mapper rules: never add tenant-related claims (INV-01). Never add a mapper that emits all realm roles.

### 10.6 User profile attributes

| Attribute | Required | User-editable | Notes |
|---|---|---|---|
| `username` | Yes | No | Usually equals email |
| `email` | Yes | Through verified flow only | `UPDATE_EMAIL` action |
| `firstName`, `lastName` | Yes | Yes | |
| `locale` | No | Yes | |

No tenant or business attributes in Keycloak.

### 10.7 Authentication flows

- Browser flow: username and password, then required second factor (OTP or WebAuthn). Passkeys preferred **(verify maturity)**.
- **Step-up:** define authentication levels and map them with ACR-to-LoA (level 1 password, level 2 password plus recent MFA). Privileged operations require level 2 ([11.6](#116-step-up-authentication)) **(verify configuration)**.
- Disable what is unused: direct grant, identity brokering, registration (until Q-01 says otherwise).

### 10.8 Invitations and onboarding

- Self-registration off. People enter through **Access invitations** ([15.4](#154-core-flows)).
- Access sends an invitation email containing an ID-only link and a separate one-time code for manual entry. For an email with no identity, Access also creates the Keycloak user (required actions set) and triggers Keycloak's time-limited HTTPS execute-actions email with a fixed redirect to the invitation acceptance page. The Keycloak link carries its own action token under the narrow INV-11 exception; it never carries the Excellore invitation code. For an existing identity, Access creates no user and sends no Keycloak action email.

### 10.9 Theming, email, configuration as code

- One login theme, `excellore`, across all apps. Theme account emails and error pages. SMTP with SPF, DKIM, DMARC.
- Realm in `infra/keycloak/` applied with OpenTofu (Keycloak provider) or `keycloak-config-cli`. Placeholders for secrets. Same configuration promotes through all environments. Manual production edits are not allowed; emergency edits MUST be exported to code the same day.

---

## 11. Authentication Flows

### 11.1 First login (creates the SSO session)

```
1.  Browser → Profile BFF      GET https://profile.excellore.com/t/acme/members
2.  BFF: no session → generate state, nonce, PKCE verifier/challenge; store returnTo
3.  BFF → Browser              302 to Keycloak /auth (client excellore-profile,
                               scope "openid profile email api-profile", state, nonce, S256)
4.  Browser → Keycloak         user authenticates (password + MFA or passkey)
5.  Keycloak                   sets its SSO cookie on auth.excellore.com
6.  Keycloak → Browser         302 to /auth/callback?code=…&state=…
7.  BFF → Keycloak (server)    code + verifier + client auth → ID, access, refresh tokens
8.  BFF                        validates ID token (sig, iss, aud, exp, nonce)
9.  BFF                        creates server-side session; sets __Host- cookie
10. BFF → Browser              302 to validated returnTo (/t/acme/members)
```

### 11.2 Tenant context and API call

```
1. Browser → BFF      GET /t/acme/members                       (cookie only)
2. BFF                session ok; refresh token if < ~60 s left
3. BFF → Access (internal, svc-profile-authz)  resolve slug "acme" for userId (from session)
                      not an active member → 404 (INV-10)       else tenantId
4. BFF → API          GET /v1/tenants/{tenantId}/members   Authorization: Bearer <user token>
5. API                validate JWT (sig via JWKS, iss, aud=api-profile, exp)
6. API (tenant gate)  resolve(sub, tenantId) via cached PDP → check permission → open tenant context
7. API → database     BEGIN; set_config('app.tenant_id', tenantId, true); query; COMMIT
8. API → BFF → Browser   JSON
```

### 11.3 SSO into another app

The user opens another app's URL. That BFF has no session, redirects to Keycloak, Keycloak finds the SSO cookie and returns a code immediately, and the app creates its own session. The user sees a redirect and no prompt. The same `/t/{slug}` path works in every app because slugs are platform-wide.

### 11.4 Token refresh

- Refresh when under ~60 s remain, or once after an API `401`.
- **Per-session lock** so concurrent requests never refresh together (rotation would treat a second refresh as theft).
- On refresh failure, destroy the session and redirect to login.

### 11.5 Authorization changes take effect without re-login

Because permissions are not in tokens, a role change takes effect when the cache entry for `(tenantId, userId)` is dropped (event) or expires (≤ 60 s). No token refresh or re-login is needed.

### 11.6 Step-up authentication

```
1. User triggers a privileged action (for example assigning a privileged role).
2. API checks the "acr" of the access token. Too low → 403 problem
   "urn:excellore:problem:step-up-required".
3. BFF redirects to Keycloak with acr_values for the higher level (MFA again).
4. Keycloak authenticates at the higher level; BFF stores new tokens.
5. User retries; the API sees the higher acr and allows it.
```

The API MUST verify the level itself from the token.

### 11.7 Account actions (Profile)

Security-critical self-service is **never implemented in our code**. The BFF redirects to Keycloak with an **application-initiated action** (`kc_action`): `UPDATE_PASSWORD`, `UPDATE_EMAIL`, `CONFIGURE_TOTP`, WebAuthn registration **(verify names)**. The BFF allow-lists the actions. Our code never sees a password or MFA secret.

### 11.8 Worker flow

```
1. API: authorize the user (permission), then publish {messageId, tenantId, userId, …}
2. Worker: consume; open tenant context for tenantId
3. Worker → Keycloak: client_credentials (worker-profile, scope api-profile) → cached token
4. Worker: do the job inside the tenant context; use userId for ownership checks and audit
```

### 11.9 Access service calling Keycloak

```
1. Admin acts in Access UI → BFF → Access API (user token, aud=api-access)
2. Access API: tenant gate, permission, anti-escalation (S1 to S12)
3. For identity operations only (create user, disable user, send actions email):
   Access API → Keycloak Admin REST using svc-access-admin
4. Access API writes an audit event (tenant_id, actor sub, target, change, reason, correlationId)
5. Access API publishes events through the outbox
```

Tenant authorization changes (roles, assignments, memberships) never touch Keycloak.

---

## 12. Logout and Session Management

### 12.1 User-initiated logout (RP-initiated)

1. Browser → BFF `POST /auth/logout` (CSRF protected).
2. BFF sends `id_token_hint` in a server-side form POST to Keycloak's discovered `end_session_endpoint`. The token MUST NOT appear in a browser URL or redirect. The browser receives only a redirect to the app root.
3. BFF deletes its local session and cookie. Keycloak ends the SSO session and sends **back-channel logout** tokens to every other BFF.
4. Other BFFs validate the token and delete matching sessions. If the Keycloak POST fails, the BFF still ends its local session and reports the global logout failure without exposing the ID token.

The Shell's **Sign out of all Excellore apps** triggers this flow.

### 12.2 Back-channel logout handler

On `POST /auth/backchannel-logout` with `logout_token`: validate signature, `iss`, `aud` (this client), `iat`, and the back-channel logout event; reject if a `nonce` is present; find sessions by `sid` (or `sub`) and delete them; return `200` or `400`. Keycloak must reach the endpoint over the network.

### 12.3 Lifetimes

| Lifetime | Value | Set in |
|---|---|---|
| Access token | 5 min | Keycloak |
| BFF idle timeout | 30 min (sliding) | BFF store TTL |
| BFF absolute max | 8 to 10 h | BFF and Keycloak |
| Authz cache | ≤ 60 s | PEP and BFF |

### 12.4 Forced logout and membership changes

- Platform security can end a user's sessions globally. Back-channel logout propagates.
- **Removing a member from a tenant** does not need a logout: the next request fails the tenant gate for that tenant (cache invalidated by event), while the person's other tenants keep working.
- **Disabling the global identity** (platform action) ends all sessions and blocks login everywhere.

---

# Part IV: Applications

## 13. Shell

### 13.1 Purpose

The front door. It makes separate apps feel like one suite and makes tenant context a first-class, visible concept.

### 13.2 Responsibilities

- Sign users in (`excellore-shell`) and show who is signed in.
- **Tenant picker** at `/`: lists the user's active tenants (from the Access internal contract). One tenant → redirect to it. Last used tenant → redirect to it.
- **Per-tenant launcher** at `/t/{slug}`: one tile per app that is **enabled for the tenant and for which the user holds `<app>.app.access`**.
- Shared chrome: logo, **tenant switcher**, app switcher, user menu (My account, sign out). Provided as a component in `@excellore/ui` and reused by Profile and Access.
- **Sign out of all apps**.
- Platform notices (maintenance banners) from static configuration.
- Pending invitations banner for the signed-in identity (read through the Access internal contract), linking to the acceptance page.

### 13.3 Non-responsibilities

No business data, no API, no database. No access tokens beyond the ID token it needs for login. A tile never enforces or grants anything (INV-02).

### 13.4 App catalog

Configuration as code: `infra/platform/app-catalog.yaml`, read at build or deploy time and shared with `@excellore/ui`.

```yaml
apps:
  - id: profile
    name: Profile
    description: Your account and your profile in this organization
    originEnv: PROFILE_ORIGIN
    urlTemplate: '{origin}/t/{slug}'
    userLevelUrl: '{origin}/account'
    icon: user
    appPermission: profile.app.access
    core: true
    order: 10
  - id: access
    name: Access
    description: People, roles, and permissions
    originEnv: ACCESS_ORIGIN
    urlTemplate: '{origin}/t/{slug}'
    icon: key
    appPermission: access.app.access
    core: true
    order: 20
```

The catalog loader resolves `originEnv` from deployment environment configuration at build or deploy time. Origins must be HTTPS; explicitly flagged local development may use HTTP on `localhost` or a `.localhost` host. Slugs are validated before URL interpolation. The UI receives only apps enabled for the tenant and backed by the member's `<app>.app.access` permission; these links remain visibility hints, not authorization decisions.

### 13.5 Technology

Next.js App Router, React, TypeScript, built on the BFF standard ([19](#19-bff-standard-nextjs)). Mostly static rendering; dynamic only for session and context.

---

## 14. Profile

### 14.1 Purpose

Two levels of "this is me":

- **My account** (global, no tenant): the person's identity-level details and security.
- **My profile in a tenant** (tenant-scoped): the person's role-specific details within one organization, plus the member directory.

### 14.2 Modules

| Module | Level | What it does | Data lives in |
|---|---|---|---|
| **Account details** | Global | Name, display name, locale, time zone | Names and locale in Keycloak (narrow update), rest in Profile DB |
| **Avatar** | Global | Upload, replace, remove | Object storage `users/{userId}/…`, metadata in Profile DB |
| **Preferences** | Global | Language, time zone, date format, notification preferences, theme | Profile DB |
| **Security** | Global | Password, authenticators, passkeys, recent sign-ins | Keycloak via `kc_action` ([11.7](#117-account-actions-profile)) |
| **Sessions** | Global | List sessions, sign out others | Keycloak sessions (own `sub` only) |
| **My data** | Global | Request data export and download | Profile worker |
| **Delete my account** | Global | Request deletion (step-up) | Event to Access |
| **Member profile** | Tenant | Job title, department, work phone, visibility | Profile DB (`tenant_member_profiles`) |
| **Member directory** | Tenant | Browse and search active members of the tenant | Profile DB |
| **Edit member profiles** | Tenant | Admins edit others' member profile fields | Profile DB, audited |

### 14.3 Data ownership

| Data | Source of truth | Table class |
|---|---|---|
| Credentials, MFA secrets | Keycloak | n/a |
| Email, username | Keycloak | n/a |
| First and last name, locale | Keycloak, mirrored for display | P |
| Display name, time zone, avatar, preferences | Profile DB | P |
| Job title, department, work phone, directory visibility | Profile DB, per tenant | T |
| Membership, roles, permissions | Access DB | (not Profile) |

**Narrow identity update.** The Profile API changes first name, last name, and locale in Keycloak through `svc-profile-identity`. A single class, `OwnIdentityService`, takes the target user **only** from the caller's token `sub` and has tests proving another user's ID cannot be supplied. The alternative (Keycloak Account REST API with the user's token) is Q-04.

**JIT provisioning.** `user_profiles` is created lazily on the first `GET /v1/me/profile`. `tenant_member_profiles` is created on `tenant.member.added.v1` (or lazily on first access) and removed on `tenant.member.removed.v1`.

### 14.4 API (audience `api-profile`)

User-level endpoints need a valid token only (any signed-in person, own data only). Tenant-scoped endpoints run the tenant gate with the listed permission.

| Method and path | Gate | Purpose |
|---|---|---|
| `GET /v1/me/profile`, `PATCH /v1/me/profile` | authenticated | Own global profile |
| `POST /v1/me/avatar/uploads`, `POST /v1/me/avatar/uploads/{id}/complete`, `DELETE /v1/me/avatar` | authenticated | Avatar |
| `GET /v1/me/preferences`, `PATCH /v1/me/preferences` | authenticated | Preferences |
| `GET /v1/me/security` | authenticated | Security summary |
| `GET /v1/me/sessions`, `DELETE /v1/me/sessions/{id}` | authenticated | Own sessions |
| `POST /v1/me/data-exports`, `GET /v1/me/data-exports/{id}` | authenticated | Export |
| `POST /v1/me/deletion-requests` | authenticated + step-up | Request account deletion |
| `GET /v1/tenants/{tenantId}/members/me` | `profile.app.access` | Own member profile |
| `PATCH /v1/tenants/{tenantId}/members/me` | `profile.app.access` | Edit own member profile |
| `GET /v1/tenants/{tenantId}/members` | `profile.member.read` | Directory (active members, cursor pagination) |
| `GET /v1/tenants/{tenantId}/members/{userId}` | `profile.member.read` | One member |
| `PATCH /v1/tenants/{tenantId}/members/{userId}` | `profile.member.manage` | Admin edit (audited) |

### 14.5 Data model (Profile database)

Classes in brackets (see [8.2](#82-table-classes)).

```
user_profiles [P]
  user_id uuid PK (Keycloak sub), display_name text, time_zone text,
  avatar_object_key text, mirror_first_name text, mirror_last_name text,
  mirror_locale text, created_at, updated_at, deleted_at

user_preferences [P]
  user_id uuid, key text, value jsonb, PRIMARY KEY (user_id, key)

tenant_member_profiles [T]   -- RLS
  tenant_id uuid, user_id uuid, job_title text, department text, work_phone text,
  directory_visibility text, status text,   -- active | suspended (mirrored from events)
  created_at, updated_at, PRIMARY KEY (tenant_id, user_id)

avatar_uploads [P]  id, user_id, object_key, status, content_type, size_bytes, created_at
data_export_jobs [P] id, user_id, status, object_key, requested_at, completed_at, expires_at
profile_audit_events [H]  id, tenant_id null, actor_sub, target_sub, action, before jsonb,
                          after jsonb, correlation_id, occurred_at     -- append-only
outbox [P]  id, type, payload jsonb, created_at, published_at
inbox [P]   event_id PK, received_at                                    -- consumer de-duplication
```

### 14.6 Worker jobs (`worker-profile`)

| Job | Trigger | Notes |
|---|---|---|
| Process avatar | Upload completed | Validate type and size, re-encode, strip EXIF, generate sizes |
| Build data export | Export requested | Gather the user's data across tenants the user belongs to (Profile data; other apps contribute through events), archive, store with expiry, notify |
| Purge expired exports | Daily | Delete past `expires_at` |
| Create member profile | `tenant.member.added.v1` | Seed row in tenant context |
| Update member status | `tenant.member.suspended.v1`, `…reactivated.v1` | Mirror status |
| Erase member profile | `tenant.member.removed.v1` | Remove tenant-scoped profile data |
| Erase tenant data | `tenant.deleted.v1` | Remove all rows for the tenant; confirm |
| Erase user data | `identity.user.deleted.v1` | Remove global profile, avatar, exports; anonymize audit; confirm |
| Refresh mirror | `identity.user.updated.v1` | Update mirrored names and locale |

### 14.7 Security notes

- Avatar uploads are untrusted: type allow-list, size limits, server-side re-encoding, never serve raw bytes, serve from a separate host or signed URLs.
- Directory visibility respects tenant settings; members never see members of other tenants.
- Admin edits of other members' profiles require `profile.member.manage` and are audited with a reason.
- Account deletion requires step-up and SHOULD have a cooling-off period.

---

## 15. Access

### 15.1 Purpose

Two faces and one authority:

- **Tenant console:** where a tenant manages **who can do what** in that tenant.
- **Platform console:** where Excellore staff provision and operate tenants.
- **Authorization authority (PDP):** the service every other service asks "what may this person do in this tenant?" ([7.8](#78-resolve-contract-pdp-and-pep)).

### 15.2 Modules

**Tenant console** (`/t/{slug}/…`, permissions in parentheses)

| Module | What it does |
|---|---|
| **My access** (`access.app.access`) | My roles, effective permissions, apps, pending requests in this tenant |
| **Members** (`access.member.read`) | List and search members, status, roles, last sign-in |
| **Invitations** (`access.member.invite`) | Invite by email with roles; resend; revoke |
| **Member actions** (`access.member.suspend`, `access.member.remove`) | Suspend, reactivate, remove |
| **Roles** (`access.role.read`, `access.role.manage`) | View system roles; create, edit, delete **custom roles**; permission picker grouped by app |
| **Assignments** (`access.assignment.manage`) | Grant or revoke roles; time-bound grants; reasons |
| **Groups** (`access.group.manage`) | Groups, membership, group role assignments (UI can ship later) |
| **Apps** (`access.apps.read`) | Which apps are enabled for the tenant; request an app |
| **Access requests** (`access.request.create`, `access.request.approve`) | Request a role or app access; approve or reject |
| **Audit log** (`access.audit.read`) | Tenant audit, search, export |
| **Settings** (`access.settings.read`, `access.settings.manage`) | Tenant name, slug, security options |
| **Support grants** (`access.supportgrant.manage`) | See and revoke platform support access |
| **Ownership and deletion** (`access.tenant.transfer`, `access.tenant.delete`) | Transfer ownership, request tenant deletion (step-up) |
| **Access reviews** (`access.review.manage`) | Periodic recertification (later phase) |

**Platform console** (`/platform/…`, platform roles from the `platform_roles` claim)

| Module | Role | What it does |
|---|---|---|
| **Tenants** | `platform-operator` | Create, edit, suspend, reactivate, start deletion; set plan |
| **Tenant apps** | `platform-operator` | Enable or disable apps, set feature limits |
| **Support grants** | `platform-support` | Request, view, and use grants |
| **Permission catalog** | all platform roles (read) | Browse synced permissions and system roles |
| **Platform audit** | `platform-auditor` | Platform-scope audit |
| **Security** | `platform-security` | Force sign-out, emergency tenant suspension |
| **Platform staff** | read-only | Shows staff and roles (managed as code) |

### 15.3 Database overview

Full DDL in [Appendix F](#appendix-f-access-database-ddl). Tables by class:

| Class | Tables |
|---|---|
| P | `tenants` (includes `authz_version`), `apps`, `permissions`, `identities`, `inbox` |
| P, RLS-exempt (tenant-keyed, see [8.2](#82-table-classes)) | `user_tenant_index`, `support_grants`, `outbox` |
| H | `roles`, `role_permissions`, `audit_events` |
| T | `tenant_members`, `role_assignments`, `groups`, `group_members`, `group_role_assignments`, `tenant_apps`, `tenant_settings`, `invitations`, `access_requests` |

`identities` is a mirror of Keycloak identities (user ID, email, names, status), populated on login and invitation and refreshed by events. It is **never listed directly**. Tenant queries reach it only through `tenant_members` joins; only platform-security endpoints may search it.

### 15.4 Core flows

**Create a tenant (platform operator)**

1. Operator calls `POST /v1/platform/tenants` with name, slug, plan, owner email, and initial apps.
2. Access validates the slug, creates the tenant in `provisioning`, enables the core apps and requested apps, and creates an **owner invitation**.
3. When the owner accepts, membership and `owner` assignment are created, the tenant becomes `active`, and `tenant.created.v1` is published.
4. *Self-serve signup* (Q-01) will reuse the same flow with the signed-in person as the owner.

**Invite and accept**

1. A member with `access.member.invite` submits email, role(s), optional groups.
2. Access creates an `invitations` row (one-time code secret stored as a hash, 7-day expiry), and sends an ID-only invitation link and the code by email. For an unknown identity, Access creates a Keycloak user with required actions and sends a time-limited Keycloak execute-actions email. For an existing identity, no Keycloak user or action email is created.
3. The invitee opens `https://access.excellore.com/invitations/accept?id=<opaque invitation ID>`, completes Keycloak's required actions if new, signs in, and enters the code. The BFF posts `{ invitationId, oneTimeCode }` in the body. Access verifies `email_verified` and **email equality** (S11).
4. Access creates the membership and assignments in one transaction, writes an audit event, publishes `tenant.member.added.v1` and `access.authz.changed.v1`.

**Create and assign a custom role**

1. Actor with `access.role.manage` (step-up if the role is privileged) creates a role: name plus permission keys.
2. Access validates S8 (no `ownerOnly`, only enabled apps, plan limit) and S1.
3. Actor with `access.assignment.manage` assigns it to a member. Access validates S1 and S2, applies, audits, and publishes `access.authz.changed.v1`.

**Remove or suspend a member**

1. Check S4 (last owner) and S5.
2. Update status, end the member's access immediately (cache invalidation event), audit, publish `tenant.member.removed.v1` or `…suspended.v1`.
3. The person's other tenants and global identity are unaffected.

**Request and approve access**

1. A member requests a role (or app access) with reason and optional end date.
2. Approvers (`access.request.approve`) are notified. On approval, Access validates S1 and applies a (time-bound) assignment.

**Transfer ownership**

Current owner (step-up) nominates a member; the nominee accepts; Access swaps owner assignments atomically and audits (S9, S4).

**Support grant**

1. Staff with `platform-support` requests a grant for a tenant (reason, role, duration ≤ 8 h).
2. The tenant owner approves (or break-glass: two `platform-security`/`platform-admin` approvals with alert).
3. Access creates a **temporary membership flagged as support** with the granted role, expiring automatically. All actions carry the grant ID in audit. The tenant owner sees and can revoke it.

**Delete a tenant**

Owner (step-up) or platform requests deletion → `pending_deletion`, all members denied, `tenant.deletion.requested.v1` → grace period → `tenant.deleted.v1` → every service erases and confirms → tombstone.

**Drift check (nightly)**

The Access worker compares Access's expected identity state (identities, disabled flags) with Keycloak and alerts on unexplained differences. (Tenant authorization data has no Keycloak counterpart to drift from.)

### 15.5 API summary

Tenant-scoped (`/v1/tenants/{tenantId}/…`, tenant gate + permission), user-level (`/v1/me/…`), platform (`/v1/platform/…`), and internal (`/internal/v1/…`). The full index is in [Appendix I](#appendix-i-api-index).

### 15.6 Worker jobs (`worker-access`)

| Job | Trigger | Notes |
|---|---|---|
| Expire assignments and support grants | Every minute | Removes at `expires_at`, bumps `authz_version`, publishes change event |
| Send emails | Events | Invitations, requests, approvals, expiry warnings |
| Bulk assignment | Admin job | Asynchronous with progress and audit, per-tenant concurrency limit |
| Drift detection | Nightly | Identity mirror vs Keycloak |
| Tenant deletion orchestration | Grace period elapsed | Publish `tenant.deleted.v1`, await confirmations, tombstone |
| Audit export | Request | Signed export to object storage with expiry |
| Access reviews | Quarterly (later phase) | Open reviews and reminders |
| Outbox publisher | Continuous | Publishes pending events |

### 15.7 Screens (tenant console, minimum)

My access · Members (search, filters, detail drawer) · Invitations · Roles (list, editor with permission picker) · Assignments · Apps · Requests inbox · Audit log · Settings · Support grants.

---

## 16. Platform Events and Inter-App Communication

### 16.1 Principles

- Events travel on the RabbitMQ topic exchange `excellore.events`. An event states a **fact that already happened**.
- Events MUST NOT carry tokens, passwords, or personal data. They carry opaque identifiers.
- Events are **versioned** (`.v1`). A breaking change adds `.v2` alongside `.v1` until consumers migrate.
- Every tenant-related event carries `tenantid` (INV-07).

### 16.2 Envelope

```json
{
  "specversion": "1.0",
  "id": "0192f0b8-…",
  "type": "tenant.member.added.v1",
  "source": "excellore/access",
  "time": "2026-01-01T12:00:00Z",
  "tenantid": "0192…",
  "subject": "user:4a1c…",
  "correlationid": "7d3f…",
  "actor": { "sub": "b19e…", "type": "human" },
  "data": { "tenantId": "0192…", "userId": "4a1c…", "membershipId": "…" }
}
```

`tenantid` is absent (null) only for global user events: identity lifecycle events, global Profile account/export/deletion events, and user-scope erasure confirmations. Every tenant-scoped event carries `tenantid`.

### 16.3 Event catalog

| Event | Producer | Consumers | Purpose |
|---|---|---|---|
| `identity.user.created.v1` / `updated.v1` / `disabled.v1` / `enabled.v1` / `deleted.v1` | Access | All services | Global identity lifecycle |
| `tenant.created.v1` / `updated.v1` | Access | All services | Tenant exists or changed (slug cache bust) |
| `tenant.suspended.v1` / `tenant.reactivated.v1` | Access | All services | Stop or resume tenant work |
| `tenant.deletion.requested.v1` | Access | All services | Prepare (stop jobs, snapshot if required) |
| `tenant.deleted.v1` | Access | All services | **Erase tenant data** |
| `tenant.app.enabled.v1` / `disabled.v1` | Access | All services | App availability changed |
| `tenant.member.added.v1` / `suspended.v1` / `reactivated.v1` / `removed.v1` | Access | All services | Membership lifecycle |
| `access.authz.changed.v1` | Access | All PEP caches | Drop cached snapshots for `{tenantId, userIds?}` |
| `access.role.created.v1` / `updated.v1` / `deleted.v1` | Access | Interested services | Role changes (informational) |
| `access.assignment.granted.v1` / `revoked.v1` | Access | Notifications | Assignment changes |
| `access.supportgrant.created.v1` / `revoked.v1` | Access | Security, notifications | Support access visibility |
| `profile.updated.v1` | Profile | Interested services | Global profile fields changed |
| `profile.deletion.requested.v1` | Profile | Access | Start account deletion flow |
| `profile.export.ready.v1` | Profile | Notifications | Export available |
| `access.erasure.confirmed.v1` / `profile.erasure.confirmed.v1` | Respective service | Access | Confirms erasure of a tenant or identity |

Routing keys follow `<domain>.<entity>.<verb>.v<N>`. Schemas are in [Appendix H](#appendix-h-event-schemas) and `Excellore.Contracts`.
Tenant metadata events contain IDs and changed field names only. Consumers resolve current names, slugs, plans, and feature values through an authorized Access contract. A `tenant.updated.v1` consumer invalidates all slug keys indexed to that tenant ID; if it has no reverse index, it clears the slug cache namespace.
Access role events are informational and contain opaque IDs only. Consumers resolve current role details and permissions through the Access authority; event payloads never grant access.
Global Profile events carry no tenant ID or tenant-scoped data. Consumers process them without tenant context and never use them to authorize tenant work (RFC-0005).

### 16.4 Delivery rules

- **At-least-once**; consumers MUST be idempotent (inbox table keyed by event `id`).
- **Transactional outbox:** write the event to `outbox` in the same transaction as the state change; a publisher sends it.
- Retry with backoff, dead-letter queues, alerts on growth.
- Schemas validated in CI.

### 16.5 Why not direct API calls

They couple availability and deployment and create hidden dependencies. The only synchronous exceptions are the Access internal contracts (INV-09). Anything else needs an ADR first.

---

## 17. Onboarding a New App

### 17.1 Checklist

| # | Step | Done when |
|---|---|---|
| 1 | One-page app brief: purpose, owner, tenant-scoped or not, data classes, dependencies | Approved by platform and security owners |
| 2 | Reserve `<app>.excellore.com`, DNS, TLS | Resolves |
| 3 | Add `permissions.yaml` ([17.2](#172-the-permission-catalog-file)) with `<app>.app.access` and feature permissions | Catalog sync test passes |
| 4 | Keycloak: `api-<app>` resource client, `api-<app>` scope (audience + roles mapper), `excellore-<app>` BFF client, `svc-<app>-authz`, `worker-<app>` if needed | Config merged in `infra/keycloak/` |
| 5 | Add the app to `app-catalog.yaml` and register it in the `apps` table (migration or platform API) | Tile appears for entitled members |
| 6 | Generate front end + BFF from `create-excellore-app`; API + worker from `dotnet new excellore-service` | Login and tenant gate work locally |
| 7 | Create the app database, roles (`<app>_migrator`, `<app>_app`), and RLS on every tenant table | RLS test suite passes |
| 8 | Subscribe to `tenant.*` and `identity.*` events; implement erasure | Deletion cascade test passes |
| 9 | Add tenant isolation and permission tests; OpenAPI marks tenant-scoped routes | CI green |
| 10 | Observability, dashboards, alerts, runbook | Listed in service inventory |
| 11 | Security checklist ([29.1](#291-checklist)) and threat model entry | Sign-off recorded |
| 12 | Enable the app for pilot tenants via the platform console | Verified end to end |

### 17.2 The permission catalog file

Each service keeps `permissions.yaml` at its root:

```yaml
app: reports
permissions:
  - key: reports.app.access
    description: Open the Reports app within a tenant
    privileged: false
    ownerOnly: false
    systemRoles: [owner, admin, member, viewer]
  - key: reports.report.create
    description: Create reports
    systemRoles: [owner, admin, member]
  - key: reports.report.delete
    description: Delete any report in the tenant
    privileged: true
    systemRoles: [owner, admin]
```

CI validates the file (keys prefixed with `app`, no duplicates, `<app>.app.access` present, no removal of a permission that is in use without `deprecated: true` first) and, on deploy, the pipeline calls `PUT /internal/v1/catalog/{app}` with the `svc-catalog-sync` identity. Access stores permissions and recomputes system role membership; custom roles are never changed by a sync.

### 17.3 Templates

- `create-excellore-app`: Next.js app with BFF, tenant resolution, shared header, CSP, CI.
- `dotnet new excellore-service`: API, worker, tests, with auth, tenant gate, RLS plumbing, outbox and inbox, health checks, telemetry.

Hand-copying code from another app is not allowed. Fix the template.

---
# Part V: Engineering Standards

## 18. Technology Stack

| Layer | Choice |
|---|---|
| **IdP** | Keycloak (latest stable line **(verify)**) + PostgreSQL. OIDC, OAuth 2.0 (code + PKCE, client credentials, optionally token exchange). TOTP and WebAuthn/passkeys |
| **Front end + BFF** | Next.js (App Router), React, TypeScript (strict), Tailwind CSS, `@excellore/ui` design system |
| **OIDC library (BFF)** | `openid-client` or `oauth4webapi` (Auth.js is an alternative) |
| **Session and cache** | Valkey (`iovalkey` or `ioredis`) |
| **Front-end utilities** | Zod, React Hook Form, TanStack Query, `server-only`, `next-intl` |
| **Monorepo** | pnpm + Turborepo (or Nx) |
| **Backend** | ASP.NET Core (Minimal APIs), EF Core + Npgsql, FluentValidation, Mapperly or hand-written mapping, Serilog |
| **Workers** | .NET Worker Service (Generic Host), Quartz.NET (Apache 2.0) |
| **Messaging** | RabbitMQ; client via Wolverine (MIT), Rebus (MIT), or the plain client. MassTransit newer majors are commercial **(verify)** |
| **Database** | PostgreSQL 16+ with row-level security. One database per service |
| **Object storage** | S3-compatible: SeaweedFS (Apache 2.0), MinIO (AGPL-3.0, check policy), or cloud storage **(verify)** |
| **Containers and orchestration** | Docker; Kubernetes (or simpler platform at small scale) |
| **Ingress** | Traefik, NGINX, or Caddy with a WAF |
| **IaC and secrets** | OpenTofu; OpenBao or Kubernetes secrets with SOPS or sealed-secrets |
| **Observability** | OpenTelemetry, Prometheus, Grafana, Loki, Tempo |
| **Testing** | xUnit, Testcontainers, Vitest, Playwright, k6, OWASP ZAP |
| **Local orchestration** | Docker Compose or .NET Aspire |

---

## 19. BFF Standard (Next.js)

### 19.1 Responsibilities

Run OIDC login, callback, logout, back-channel logout. Hold sessions and tokens server-side. Resolve the tenant from the URL slug and verify membership. Attach tokens to API calls and handle refresh. Protect pages and endpoints. Defend against CSRF.

### 19.2 Required routes

| Route | Method | Purpose |
|---|---|---|
| `/auth/login` | GET | Start OIDC with validated `returnTo` |
| `/auth/callback` | GET | Code exchange, create session |
| `/auth/logout` | POST | Destroy session, redirect to Keycloak end-session |
| `/auth/backchannel-logout` | POST | Validate `logout_token`, delete sessions |
| `/auth/session` | GET | Non-sensitive session summary |
| `/auth/step-up` | GET | Re-authenticate at a higher ACR, then return |
| `/auth/account-action` | GET | Redirect to Keycloak with an allow-listed `kc_action` |
| `/t/[slug]/…` | any | Tenant-scoped pages (slug resolved and verified) |
| `/api/t/[slug]/…` | any | Proxy to the app API: `/v1/tenants/{tenantId}/…` |
| `/api/me/…` | any | Proxy to the app API: `/v1/me/…` |

### 19.3 Session record

```json
{
  "sid": "random-256-bit-id", "kcSid": "keycloak-session-id", "sub": "user-uuid", "acr": "2",
  "accessToken": "…", "accessTokenExpiresAt": 1700000000,
  "refreshToken": "…", "idToken": "…",
  "createdAt": 1700000000, "lastSeenAt": 1700000100
}
```

Stored in Valkey under the app's key prefix, TTL aligned to SSO max, tokens encrypted at rest, secondary index `kcSid → sid`, session ID regenerated at login. **No tenant is stored in the session** (INV-01, INV-04): the tenant comes from the URL on each request.

### 19.4 Tenant resolution helper

```ts
// lib/tenant.ts  (server-only)
import 'server-only';
import { notFound } from 'next/navigation';

export async function requireTenant(slug: string) {
  const session = await requireSession();                       // redirects to login if absent
  const ctx = await accessContext.resolveSlug(session.sub, slug); // internal contract, cached ≤ 60 s
  if (!ctx) notFound();                                         // 404 for non-members (INV-10)
  return ctx;                                                   // { tenantId, slug, name }
}
```

UI visibility (hide a button the user cannot use) MAY use the permission list from the context, but it is a hint only.

### 19.5 Data-access layer

All server code that calls an API goes through `lib/api-client.ts`:

1. Get the session; redirect if absent. 2. Refresh if needed (per-session lock). 3. Call the API with `Authorization: Bearer`. 4. Never cache authenticated responses across users (`no-store` or per-user keys). 5. Translate RFC 9457 problems into typed errors, including `step-up-required`.

Mark it `import 'server-only'`.

### 19.6 Route protection, CSRF, headers

- Middleware (named `proxy` in newer Next.js **(verify)**) MAY do a cheap redirect when no session cookie exists. It MUST NOT be the only check. The real check is in the data-access layer, Route Handlers, and Server Actions. Server Actions are public POST endpoints.
- CSRF: `SameSite=Lax`, a custom header on state-changing `/api/*` requests, `Origin`/`Host` verification, logout as POST.
- Headers: strict nonce-based `Content-Security-Policy`, `Strict-Transport-Security`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy`, `frame-ancestors 'none'`.

### 19.7 Shared package `@excellore/auth`

Exports: OIDC client, session store, route handler factories (login, callback, logout, back-channel logout, step-up, account action), `requireSession()`, `requireTenant()`, `getAccessToken()`, the Access internal client (`resolveSlug`, `listTenants`, `resolve`), CSRF and cookie utilities, and a Zod config schema that fails fast on missing environment. An app supplies only client ID, secret, redirect URIs, scopes, Valkey prefix, and API base URL.

---

## 20. API Standard (.NET)

### 20.1 Authentication

```csharp
builder.Services
  .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
  .AddJwtBearer(o =>
  {
      o.Authority = builder.Configuration["Auth:Authority"];   // https://auth.excellore.com/realms/excellore
      o.Audience = "api-profile";                              // "api-access" in Access
      o.RequireHttpsMetadata = true;
      o.MapInboundClaims = false;
      o.TokenValidationParameters = new TokenValidationParameters
      {
          ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
          ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(30),
          NameClaimType = "preferred_username", RoleClaimType = "roles"
      };
  });

builder.Services.AddAuthorization(o =>
{
    // Machine and platform policies only. Tenant permissions use RequirePermission (below).
    o.AddPolicy("Internal.Authz",     p => p.RequireRole("access.authz.resolve"));
    o.AddPolicy("Internal.Catalog",   p => p.RequireRole("access.catalog.sync"));
    o.AddPolicy("Platform.Operator",  p => p.RequireClaim("platform_roles", "platform-operator", "platform-admin"));
    o.AddPolicy("Platform.Support",   p => p.RequireClaim("platform_roles", "platform-support", "platform-admin"));
    o.AddPolicy("Platform.Auditor",   p => p.RequireClaim("platform_roles", "platform-auditor", "platform-admin"));
    o.AddPolicy("Platform.Security",  p => p.RequireClaim("platform_roles", "platform-security", "platform-admin"));
});
```

### 20.2 Tenant gate and `RequirePermission` (shared library `Excellore.Authz`)

```csharp
// Endpoint declaration
var tenant = app.MapGroup("/v1/tenants/{tenantId:guid}")
                .RequireAuthorization()
                .AddEndpointFilter<TenantGateFilter>();      // MANDATORY for any route with {tenantId}

tenant.MapGet("/members", ListMembers).RequirePermission("profile.member.read");
tenant.MapPatch("/members/{userId:guid}", UpdateMember).RequirePermission("profile.member.manage");
```

```csharp
public sealed class TenantGateFilter(
    IAuthzResolver resolver, ICurrentUser user, ITenantContextAccessor tenantContext)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!Guid.TryParse(http.Request.RouteValues["tenantId"]?.ToString(), out var tenantId))
            return Problems.NotFound();

        var required = http.GetEndpoint()!.Metadata.GetOrderedMetadata<RequirePermissionMetadata>()
                           .SelectMany(m => m.Permissions).ToArray();

        var snapshot = await resolver.ResolveAsync(user.UserId, tenantId, http.RequestAborted);

        if (snapshot.Decision != Decision.Allow) return Problems.NotFound();      // non-member or inactive tenant (INV-10)
        if (required.Any(p => !snapshot.Permissions.Contains(p))) return Problems.Forbidden();

        if (required.Any(snapshot.IsPrivileged) && !user.HasRecentMfa)
            return Problems.StepUpRequired();                                     // 403 step-up-required

        tenantContext.Set(tenantId, snapshot);                                    // opens logging scope + RLS context
        return await next(ctx);
    }
}
```

- A **startup test** enumerates all endpoints and **fails the build** if any route containing `{tenantId}` lacks the tenant gate or a `RequirePermission` (or an explicit `AllowAnyMember` marker).
- User-level endpoints (`/v1/me/…`) require authentication and operate only on the caller's `sub`.
- Platform endpoints use the `Platform.*` policies and never open a tenant context.

### 20.3 Tenant unit of work (RLS context)

```csharp
public sealed class TenantUnitOfWork(AppDbContext db, ITenantContextAccessor tenant)
{
    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenant.TenantId.ToString()}, true)", ct);   // transaction-local
        var result = await work();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
}
```

A request filter wraps every tenant-scoped handler in this unit of work. `SaveChanges` stamps `TenantId` and throws on mismatch; a global query filter adds `TenantId == context` to every class T entity ([8.4](#84-application-pattern-net-ef-core)).

### 20.4 Current user and tenant context

```csharp
public interface ICurrentUser    { Guid UserId { get; } string? Email { get; } string? Acr { get; } bool HasRecentMfa { get; } }
public interface ITenantContext  { Guid TenantId { get; } AuthzSnapshot Snapshot { get; } }
```

Handlers depend on these, never on `HttpContext`.

### 20.5 Conventions (all APIs)

| Topic | Standard |
|---|---|
| Route shapes | `/v1/tenants/{tenantId}/…` (tenant-scoped), `/v1/me/…` (user-level), `/v1/platform/…` (platform), `/internal/v1/…` (Access internal) |
| Versioning | URL prefix `/v1`; breaking change means `/v2` alongside |
| Format | JSON, `camelCase`, UTF-8 |
| IDs | UUID (v7 preferred, `Guid.CreateVersion7()` **(verify)**) |
| Time | UTC ISO 8601, field names end in `At` |
| Errors | RFC 9457 `application/problem+json`, stable `type` URN, `traceId` |
| Pagination | Cursor: `?limit=50&cursor=…` → `nextCursor` |
| Idempotency | `Idempotency-Key` on creating or triggering `POST`s |
| Concurrency | `ETag` and `If-Match` on updatable resources |
| Correlation | Accept and propagate `traceparent`; return `X-Correlation-Id` |
| Status codes | `401` bad token; `404` unknown **or non-member tenant/resource**; `403` member lacks permission or step-up needed; `409` conflict; `422` validation; `429` rate limited; `503` authorization unavailable (writes) |
| OpenAPI | Generated per build; routes containing `{tenantId}` carry `x-excellore-tenant-scoped: true`; breaking-change check in CI |
| Health | `/health/live`, `/health/ready` on an internal path |

Problem types: `urn:excellore:problem:{validation | forbidden | not-found | step-up-required | conflict | rate-limited | authz-unavailable | quota-exceeded}`.

### 20.6 Additional rules

- Reject tokens without the expected `aud`; optionally verify `azp`.
- Resource-level checks follow permission checks, in handler code.
- Rate limit per user and per tenant; request size limits.
- CORS stays disabled (browsers never call APIs).
- Never log tokens, secrets, or request bodies that may hold personal data.

### 20.7 Solution structure (per service)

```
services/<app>/
├─ Excellore.<App>.Api/            # endpoints, auth wiring, tenant gate registration
├─ Excellore.<App>.Worker/         # worker host, consumers, schedules
├─ Excellore.<App>.Application/    # use cases, handlers, validators
├─ Excellore.<App>.Domain/         # entities and rules, no framework dependencies
├─ Excellore.<App>.Infrastructure/ # EF Core, RLS, outbox/inbox, messaging, storage
├─ permissions.yaml
├─ migrations/                     # SQL or EF migrations incl. RLS
└─ tests/
```

### 20.8 Resolver client and cache (`Excellore.Authz`)

`IAuthzResolver.ResolveAsync(userId, tenantId)`: in-memory layer (≤ 10 s) → Valkey (`authz:{tenantId}:{userId}`, 60 s) → `POST /internal/v1/authz/resolve` with a cached `svc-<app>-authz` token. Subscribes to `access.authz.changed.v1` to drop entries. Fail-closed rules from [7.8](#78-resolve-contract-pdp-and-pep): writes `503` when unavailable, reads may use a snapshot up to 5 minutes old.

### 20.9 Keycloak admin client (Access and Profile only)

Access: `IIdentityAdmin` (create user with required actions, send execute-actions email, disable, enable). Every method requires an `Actor` (human `sub`, `acr`) so audit data cannot be omitted. Authentication uses the `svc-access-admin` token. Wrap with retry, timeout, and circuit breaker. Profile: `OwnIdentityService` only ([14.3](#143-data-ownership)).

---

## 21. Worker and Messaging Standard

### 21.1 Principles

- Workers authenticate as themselves (client credentials). Messages never contain tokens (INV-07).
- Every tenant-related message carries `tenantId`; the worker opens tenant context before touching data.
- The API authorizes the user before publishing; the worker re-validates ownership **inside the tenant context** (RLS prevents cross-tenant reads).
- System-wide schedules iterate tenants and process each in its own context and transaction.

### 21.2 Command envelope (private to a service)

```json
{ "messageId": "uuid", "correlationId": "uuid", "type": "profile.export.build.v1",
  "occurredAt": "2026-01-01T12:00:00Z", "tenantId": "uuid-or-null", "userId": "user-sub-uuid",
  "payload": { } }
```

Domain events use the envelope in [16.2](#162-envelope).

### 21.3 Handler requirements

Idempotent (inbox on `messageId` or event `id`); transactional outbox for publishing; retry with backoff then dead-letter; poison messages parked with alerts; graceful shutdown; `traceparent` and `tenant.id` propagated into logs and spans; per-tenant concurrency limits for expensive jobs.

### 21.4 Worker authentication

Use a client-credentials token provider with a semaphore-protected cache (refresh 30 s before expiry), scope `api-<own>`, attached through a `DelegatingHandler`. If the worker reaches its own database directly, it needs no token for that.

### 21.5 Scheduled jobs

Quartz.NET with clustering or a distributed lock so replicas do not double-run jobs. Scheduled jobs run with the machine identity and **no user context**.

### 21.6 Calling another service as the user

Prefer events and local read models. If unavoidable, use token exchange (RFC 8693) **(verify)** and write an ADR (INV-09).

---

## 22. Data Standards

### 22.1 Classification

| Class | Name | Examples | Handling |
|---|---|---|---|
| 0 | Public | Marketing pages, app catalog | None |
| 1 | Internal | Role catalog, audit metadata without personal data | Authenticated |
| 2 | Personal (PII) | Name, email, phone, avatar, IP address | Minimize, access-logged, encrypted at rest |
| 3 | Sensitive | Credentials, tokens, secrets, MFA data | Never stored by apps; tokens only in the encrypted session store; never logged |

### 22.2 Rules

- One database per service; no other service connects to it.
- Every table is classified (P, H, T) in its migration; class T and H tables have RLS ([8.2](#82-table-classes)).
- Migrations are versioned, run by the `<app>_migrator` role as a separate pipeline step, and backward compatible for one release (expand, migrate, contract).
- TLS in transit everywhere; disks and backups encrypted; class 2 columns SHOULD use application-level encryption where risk warrants.
- **Retention: placeholder (Q-11).** Detailed retention periods and policies are deferred until the platform is stable enough to use. Until then, do not build retention schedules, archival jobs, or automatic purging beyond what the deletion flows in [22.3](#223-tenant-and-user-deletion) already require. Keep audit data append-only and do not delete it automatically.
- Non-production environments never contain real personal data unless anonymized.
- Log identifiers, not payloads; mask emails and phone numbers.

### 22.3 Tenant and user deletion

**Tenant deletion** (`tenant.deleted.v1`): every service deletes all rows with that `tenant_id` (in tenant context, in batches), removes `tenants/{tenantId}/…` objects, clears cache keys, and publishes `<service>.erasure.confirmed.v1`. Access waits for all confirmations before writing the tombstone. Handling of audit rows is a placeholder (Q-11): until decided, keep them and anonymize personal fields only if erasure of a person is requested.

**User deletion** (`identity.user.deleted.v1`): every service removes global user data and **anonymizes** user references inside tenant data that must be kept (for example `created_by` becomes a tombstone ID), then confirms.

**Tenant export:** a tenant owner can request an export of tenant data (Access exports its data; other services contribute through events). Frequency limited per plan.

---

## 23. Frontend Standards

| Topic | Standard |
|---|---|
| Design system | `@excellore/ui`: tokens, components, shared header with tenant switcher and app switcher |
| Accessibility | WCAG 2.2 AA target; keyboard navigation, focus management, labels, contrast |
| Internationalization | All strings externalized; locale from Profile or `Accept-Language`; per-user time zone |
| Errors | Typed errors from the data-access layer; friendly messages; global error boundary; a clear "not found" for non-member tenants (no hints) |
| Forms | Zod schemas shared with the server; server errors mapped to fields |
| State | Server state through server components or TanStack Query; avoid global client stores |
| Tenant context | Always visible in the header; every tenant-scoped URL starts with `/t/{slug}`; never store tenant in `localStorage` as authority |
| Security | No `dangerouslySetInnerHTML` without sanitization; no secrets in bundles; strict CSP |
| Performance | Route budgets (JS size, LCP) checked with Lighthouse in CI |
| Dependencies | Pinned versions, reviewed additions, audits in CI |

---

## 24. Repository, Branching, and Ownership

### 24.1 Layout

```
excellore/
├─ AGENTS.md                         # agent entry point (Appendix J)
├─ apps/
│  ├─ shell/    ├─ profile/    └─ access/          # Next.js UI + BFF
├─ packages/
│  ├─ excellore-auth/   ├─ ui/   └─ config/
├─ services/
│  ├─ profile/          # Excellore.Profile.*  + permissions.yaml
│  └─ access/           # Excellore.Access.*   + permissions.yaml
├─ shared/dotnet/
│  ├─ Excellore.Auth/        # JWT setup, ICurrentUser, token client, step-up
│  ├─ Excellore.Authz/       # RequirePermission, TenantGateFilter, resolver, cache
│  ├─ Excellore.Platform/    # tenant context, RLS unit of work, outbox/inbox, problems, telemetry
│  └─ Excellore.Contracts/   # event and command schemas
├─ infra/
│  ├─ keycloak/              # realm, theme, platform-staff.yaml
│  ├─ platform/              # app-catalog.yaml
│  ├─ tofu/   ├─ k8s/   └─ compose/
├─ docs/
│  ├─ handbook.md            # this book
│  ├─ adr/   └─ runbooks/
├─ turbo.json  ├─ pnpm-workspace.yaml  └─ Excellore.sln
```

### 24.2 Branching and review

While @saficool is the only maintainer, use small direct commits to `main` (ADR-0017). Run the full local build, lint, test, formatting, and secret checks before each push, then verify the `javascript`, `dotnet`, `handbook`, and `secrets` CI jobs on `main`. Fix a failed job immediately with a follow-up commit; do not force-push or delete `main`. Signed commits are recommended. Use a short-lived branch and PR when a change explicitly requires independent approval, such as production platform-staff assignments.

`CODEOWNERS` records @saficool as the platform security owner for `infra/keycloak/`, `shared/dotnet/Excellore.Auth`, `shared/dotnet/Excellore.Authz`, `shared/dotnet/Excellore.Platform`, `packages/excellore-auth`, `services/access/`, every `permissions.yaml`, and `infra/db/rls-exempt.txt`. Before a direct push, the author reviews the full diff and records affected invariants and test evidence in the commit body. For security-owned paths, the author also records authentication, authorization, tenant-isolation, secret-handling, and audit implications; uses an ADR for a security decision; and resolves failed local checks before pushing. The PR template remains available for changes that require a PR. When another maintainer with write access joins, revisit ADR-0017 and require independent code-owner approval for security-owned paths.

### 24.3 Ownership

| Area | Owner role |
|---|---|
| Handbook, shared libraries, Keycloak configuration, Shell | Platform team |
| Access | Platform team with security oversight |
| Profile | Profile team |
| Infrastructure and operations | Platform / SRE |
| Security reviews, threat model | Security owner |

Fill in named owners and on-call rotations in the service inventory.

---

## 25. Testing Strategy

### 25.1 Levels

| Level | What | Tools |
|---|---|---|
| Unit | Permission resolution, anti-escalation (S1 to S12), claims, session store, refresh lock, `OwnIdentityService` | xUnit, Vitest |
| Integration (API) | JWT validation, audience rejection, tenant gate, step-up, problem details | `WebApplicationFactory` + Testcontainers (Keycloak, Postgres, RabbitMQ, Valkey) |
| Integration (BFF) | Login callback, refresh with lock, back-channel logout, slug resolution | Vitest + Keycloak container |
| **Tenant isolation** | See 25.2 | xUnit + Testcontainers |
| Contract | Event schemas, OpenAPI diff, permission catalog validation | Schema validation in CI |
| End to end | Login, SSO, tenant switching, invitation to first login, logout everywhere | Playwright against the local stack |
| Security | Wrong audience, tampered token, forged tenant ID, open redirect, CSRF, privilege escalation | Targeted tests, OWASP ZAP |
| Load | Login burst, authz resolve, refresh storm, bulk assignment | k6 |
| Resilience | Keycloak, broker, Valkey, Access authority down | Fault injection in staging |

### 25.2 Tenant isolation test harness

Provided by `Excellore.Platform.Testing`; **every service MUST run it** (INV-16).

**Fixture:** tenants `T1` and `T2`; users `U1` (member of T1), `U2` (member of T2), `U12` (member of both with different roles), `U0` (member of neither); resources created in each tenant.

**API checks (automatic).** The harness enumerates OpenAPI operations with `x-excellore-tenant-scoped: true` and, for each, asserts:

| Caller | Request | Expected |
|---|---|---|
| `U0` | any `T1` route | `404` |
| `U2` | `T1` route | `404` |
| `U2` | `T2` route with a **`T1` resource ID** | `404` |
| `U12` | `T2` route with a `T1` resource ID | `404` |
| `U1` (member, lacks permission) | `T1` route needing a permission they lack | `403` |
| Any | route with a forged or unknown `tenantId` | `404` |
| Suspended-tenant member | any route | `404` |
| Removed member | any route (after cache invalidation event) | `404` |

**Database checks (automatic).** Connected as `<app>_app`:

```sql
-- Meta-test: every table with tenant_id has RLS enabled AND forced. Must return zero rows.
SELECT c.relname
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
JOIN information_schema.columns col
  ON col.table_schema = n.nspname AND col.table_name = c.relname AND col.column_name = 'tenant_id'
WHERE n.nspname = 'public' AND c.relkind = 'r'
  AND c.relname <> ALL (@exempt)            -- tables listed in infra/db/rls-exempt.txt
  AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity);
```

Then, for each class T table: with context `T2`, `SELECT` returns zero `T1` rows; `INSERT`/`UPDATE` of `T1` rows fails; with **no context**, `SELECT` returns zero rows. Also assert the app role has no `BYPASSRLS` and does not own tables.

**Startup route audit.** Fails if any route containing `{tenantId}` lacks the tenant gate.

### 25.3 Scenarios that MUST be automated

1. Sign in at Shell, open Profile and Access without a prompt (SSO).
2. A token for `api-profile` is rejected by the Access API, and the reverse.
3. A person in tenants A and B sees different roles and apps in each, and nothing from the other.
4. A role change takes effect within 60 s without re-login; removal from a tenant is immediate after the event.
5. Custom roles: create, assign, permissions take effect; S1, S2, S4, S8 enforced.
6. A privileged operation fails without step-up (`403 step-up-required`) and succeeds after it.
7. App not enabled for the tenant: its permissions are ineffective and its tile is absent.
8. Invitation: unknown email, known email, wrong-email acceptance rejected, expired and reused tokens rejected.
9. Suspended tenant and `pending_deletion`: all members denied.
10. Tenant deletion erases data in every service and produces confirmations.
11. User deletion anonymizes references across tenants.
12. Worker jobs run in the right tenant context and cannot read other tenants.
13. Support grant: time-boxed, scoped, audited, revocable by the owner; none by default.
14. Logout in any app ends the session in all apps.
15. Authority unavailable: writes `503`, reads served from cache ≤ 5 min, then denied.

---

## 26. CI/CD and Release

### 26.1 Pipeline stages (per deployable)

1. Restore, build, lint, type-check.
2. Unit tests.
3. Integration tests and **tenant isolation harness**.
4. Permission catalog validation and OpenAPI breaking-change check.
5. Security: dependency audit, SAST, secret scan (Gitleaks), container scan (Trivy).
6. Build image, SBOM, sign image (recommended).
7. Deploy to dev; staging on merge; production with approval.
8. **Catalog sync** (`svc-catalog-sync`) after the service is healthy.
9. Post-deploy smoke tests and E2E.

### 26.2 Rules

- Affected-only builds (Turborepo or Nx).
- Realm and infrastructure changes by pipeline only.
- Database migrations run before new traffic and follow expand, migrate, contract. A **migration lint** fails any migration that adds a `tenant_id` table without RLS or creates a table without a class comment.
- **Deploy order for breaking auth changes:** Keycloak config (additive) → Access → APIs → BFFs → remove old configuration.
- Progressive delivery with automatic rollback on error-rate or latency regression.
- Every release is traceable to a commit, a pipeline run, and an approver.

---

# Part VI: Operations

## 27. Infrastructure and Deployment

### 27.1 Environments

| Environment | Purpose | Keycloak |
|---|---|---|
| Local | Developer machine | Realm imported from `infra/keycloak/realm-dev.json` |
| Dev | Integration | Separate instance, dev secrets |
| Staging | Production mirror | Separate instance |
| Production | Live | HA, production secrets |

Secrets, clients, and signing keys are never shared between environments.

### 27.2 Keycloak in production

At least two instances behind a load balancer with distributed cache per Keycloak docs; PostgreSQL with PITR; explicit hostname and proxy headers; metrics scraped by Prometheus; rehearsed upgrades (staging first, back up, roll one node at a time); scheduled signing key rotation. Capacity-test login bursts (start of business day across time zones).

### 27.3 Network

```
Internet → Ingress / WAF (TLS)
   ├─ excellore.com, profile.…, access.…  → BFFs
   └─ auth.excellore.com                  → Keycloak (public paths only)
Private network
   BFFs     → own API; Access internal contracts; Valkey
   APIs     → own PostgreSQL; RabbitMQ; object storage; Access internal contracts; Keycloak admin (Access, Profile)
   Workers  → RabbitMQ; own PostgreSQL; own API; Keycloak token endpoint
   Keycloak → BFF back-channel logout endpoints
   Access   → Keycloak admin API; own PostgreSQL
```

Network policies enforce these paths. Block `/admin`, the `master` realm, and `/internal/**` on the public ingress.

### 27.4 Database roles (per service)

```sql
CREATE ROLE access_migrator LOGIN;            -- owns schema objects, runs migrations only
CREATE ROLE access_app      LOGIN NOBYPASSRLS; -- runtime role: no ownership, no bypass
GRANT USAGE ON SCHEMA public TO access_app;
-- after migrations:
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO access_app;
ALTER DEFAULT PRIVILEGES FOR ROLE access_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO access_app;
-- append-only tables: REVOKE UPDATE, DELETE ON audit_events FROM access_app;
```

Runtime roles never receive superuser, `BYPASSRLS`, or ownership. Break-glass database access is time-limited, audited, and separate.

### 27.5 Data stores

| Store | Used by | Notes |
|---|---|---|
| PostgreSQL (Keycloak) | Keycloak | Separate |
| PostgreSQL `profile`, `access` | Respective service and worker | RLS; `audit_events` append-only |
| Valkey | BFFs and PEP caches | Separate key prefixes per app; replication per availability needs |
| RabbitMQ | Profile, Access | Exchange `excellore.events`; per-service queues and vhosts |
| Object storage | Profile, Access | Private buckets; keys by tenant and user; signed URLs |

### 27.6 Scaling

BFFs and APIs are stateless and scale horizontally. Workers scale by queue depth. Keycloak scales by instance count and cache tuning. The Access authority is the most critical dependency: run it with multiple replicas, tight resolve latency targets, and heavy caching in PEPs.

---

## 28. Observability and SLOs

### 28.1 Telemetry

- **Tracing:** OpenTelemetry end to end; propagate `traceparent` BFF → API → broker → worker.
- **Tenant attribution:** every span and log record in tenant context carries `tenant.id` (opaque ID). Metrics use `tenant.id` only for bounded sets (top-N tenants) to avoid cardinality explosions.
- **Logs:** structured (Serilog, pino) into Loki with `traceId`, `userId` (`sub`), `tenant.id`, `correlationId`. Never tokens, secrets, cookies.
- **Dashboards:** per app; platform identity (logins, failures, refresh errors); **authorization** (resolve latency, cache hit rate, denial rates, change-event lag); per-tenant top talkers.

### 28.2 Proposed SLOs (tune after measuring)

| Service | SLI | Target |
|---|---|---|
| Keycloak login and token | Availability | 99.9% monthly |
| Keycloak token endpoint | p95 latency | < 300 ms |
| **Access resolve endpoint** | Availability / p95 latency | 99.95% / < 50 ms (cache hit path < 5 ms) |
| BFF authenticated page load | p95 server time | < 500 ms |
| APIs read | p95 latency | < 300 ms |
| APIs | 5xx rate | < 0.5% |
| Event delivery | Publish to consume p95 | < 5 s |
| Permission change propagation | Change to enforced everywhere | < 60 s (typical < 5 s) |
| Back-channel logout | Keycloak logout to BFF removal | < 30 s |

### 28.3 Alerts (minimum)

Failed-login spikes; refresh failures; elevated `401`/`403`/`404` rates per API; **spikes of tenant-gate denials from one user or IP (probing)**; resolve endpoint errors or latency; cache invalidation lag; Keycloak latency and node loss; queue backlog and dead-letter growth; any use of `platform-admin`; any support grant created; any `svc-*` token used from an unexpected host; audit hash-chain failure; tables found without RLS (scheduled check); certificate expiry < 21 days; secrets older than policy.

---

## 29. Security

### 29.1 Checklist

**Identity provider**

- [ ] Dedicated `excellore` realm; `master` not exposed; admin console internal with MFA.
- [ ] Brute-force detection, password policy, email verification on; MFA required; passkeys offered.
- [ ] Self-registration off (until Q-01 changes it).
- [ ] Exact redirect URIs; implicit and password grants off; Full scope allowed off.
- [ ] **No tenant claims or tenant roles in any mapper (INV-01).**
- [ ] Refresh token rotation on; HA, backups, prompt patching; realm in version control; events exported.
- [ ] Platform staff roles managed only by code.

**BFF**

- [ ] Code + PKCE (S256); `state` and `nonce` validated; tokens only in the encrypted server-side store.
- [ ] `__Host-` cookie, `HttpOnly`, `Secure`, `SameSite`; session ID regenerated at login.
- [ ] CSRF defenses; `returnTo` allow-list; `kc_action` allow-list; `server-only` guards.
- [ ] Slug resolution returns `404` for non-members; no tenant stored in session.
- [ ] Back-channel logout implemented; strict CSP and headers; auth never only in middleware.

**API**

- [ ] Validates issuer, audience, signature, lifetime.
- [ ] **Every `{tenantId}` route passes the tenant gate (startup audit).**
- [ ] Permission checks plus resource-level checks; step-up enforced from `acr`.
- [ ] RLS context set per request in a transaction; runtime DB role has no bypass.
- [ ] Not public; rate limits per user and tenant; no tokens or PII in logs.

**Workers and messaging**

- [ ] Client credentials per worker; no tokens in messages; `tenantId` in every tenant message.
- [ ] Idempotent handlers, outbox/inbox, dead-letter queue; broker authenticated and encrypted.

**Access-specific**

- [ ] Safeguards S1 to S12 implemented and tested.
- [ ] Audit append-only with hash chain and immutable copy; `tenant_id` on every event.
- [ ] Support grants time-boxed, audited, owner-visible.
- [ ] `svc-access-admin` rotated and usage-alerted; internal endpoints private and rate-limited.

**Platform**

- [ ] TLS everywhere (mTLS or mesh where feasible); secrets in a manager with rotation.
- [ ] Network policies; dependency, image, and secret scanning in CI; restore-tested backups.
- [ ] Scheduled check that every `tenant_id` table has forced RLS.

### 29.2 Threat model summary

| Threat | Example | Mitigations |
|---|---|---|
| **Cross-tenant IDOR** | User from T2 requests T1's resource by ID | Tenant gate, RLS, global query filter, `404`, isolation harness |
| **Tenant confusion** | Forged tenant header or body field | Tenant only from route; verified by the gate; no trust in headers or tokens |
| **RLS bypass** | Privileged DB role, missing `FORCE`, session-level setting leaking across pooled connections | Non-owner runtime role, `FORCE`, transaction-local `set_config`, meta-tests |
| **Stale permissions** | Removed member keeps access | Event invalidation, 60 s TTL, hard 5-min stale limit for reads only |
| **Tenant enumeration** | Probing slugs or IDs | `404` for non-members, uniform invitation responses, rate limits, probing alerts |
| **Custom role escalation** | Admin crafts a role with powers they lack | S1, S2, S8, step-up, audit |
| **Invitation hijack** | Code theft or wrong person accepts | ID-only link, separately entered hashed single-use code, verified-email equality, expiry; Keycloak action link is HTTPS and time-limited |
| **Support access abuse** | Staff browse tenant data | No standing access, time-boxed grants, owner visibility, audit with grant ID |
| **Catalog tampering** | Malicious `permissions.yaml` grants broad rights | Recorded owner security review, CI validation, default-deny, `systemRoles` review |
| **Noisy neighbor** | One tenant degrades others | Per-tenant rate limits, quotas, concurrency caps |
| Token theft from browser | XSS | No tokens in browser, strict CSP, HttpOnly cookie |
| Session hijack | Stolen cookie | `__Host-` cookie, short idle, step-up, session list in Profile |
| CSRF / open redirect | Cross-site POST, malicious `returnTo` | `SameSite`, custom header, allow-lists, exact redirect URIs |
| Confused deputy | Token for one API on another | Per-API audience |
| Compromised BFF or worker | Attacker controls one | Isolated client and audience, minimal machine roles, network policies |
| Compromised `svc-access-admin` | Keycloak admin API abuse | Narrow roles (users only), network restriction, alerts, rotation, drift detection |
| Compromised Keycloak | IdP takeover | Hardening, internal admin, key rotation, monitoring, tested recovery |
| Compromised Access authority | Wrong decisions for everyone | Heavy review, append-only audit, DB-level RLS as backstop for data, anomaly alerts |
| Message tampering or replay | Forged event | Authenticated broker, idempotent consumers, schema validation |
| Account takeover | Credential stuffing | MFA, brute-force detection, breached-password checks |
| Supply chain | Malicious dependency | Pinned versions, audits, SBOM, signed images |

### 29.3 Secrets and keys

| Secret | Rotation | Storage |
|---|---|---|
| BFF, worker, `svc-*` client secrets | 90 days (or `private_key_jwt`) and on suspicion | Secrets manager |
| Session encryption key | Annual, versioned | Secrets manager |
| Keycloak realm signing keys | Per policy with overlap | Keycloak, backed up |
| Database credentials | 90 days or dynamic | Secrets manager |

### 29.4 Privileged identities

| Identity | Holder | Controls |
|---|---|---|
| `platform-admin` | Named individuals, minimum number | Granted via reviewed pull request, alert on use, quarterly review |
| Tenant `owner` | Per tenant | Step-up for sensitive actions, last-owner protection |
| Keycloak `master` admins | Platform team | Internal only, MFA, break-glass |
| `svc-access-admin` | Access service | Network-restricted, rotated, usage alerts |
| Database superusers | SRE | Break-glass, time-limited, audited |

### 29.5 Compliance mapping (placeholder, Q-11)

Map GDPR, SOC 2, ISO 27001 controls to: audit log, tenant deletion and export ([22.3](#223-tenant-and-user-deletion)), isolation tests, encryption, incident response, access reviews, data residency (Q-05).

---

## 30. Incident Response and Runbooks

| Severity | Definition | Response |
|---|---|---|
| SEV1 | Suspected cross-tenant data exposure; compromise of Keycloak, signing keys, `svc-*`, or the Access authority; full login outage | Page on-call; incident commander; consider suspending affected tenants |
| SEV2 | Partial outage; privileged anomaly | Page on-call; escalate in hours |
| SEV3 | Degraded performance, single-user issues | Ticket |

Required runbooks: Keycloak outage · **Access authority outage** (what degrades, cache behavior) · **Suspected cross-tenant leak** (freeze, scope, notify, evidence) · Signing key compromise · Client secret leak · `svc-access-admin` abuse · Mass session revocation · Compromised admin or owner account · Emergency tenant suspension · Dead-letter growth · Valkey outage · Broker outage · Database restore (including **single-tenant restore**) · Certificate expiry · Onboarding a new app ([17](#17-onboarding-a-new-app)).

Post-incident: blameless review within five working days with timeline, root cause, actions, and handbook or ADR updates. Cross-tenant exposure incidents also trigger the legal and customer notification process.

---

## 31. Backup and Disaster Recovery

| Asset | Backup | Targets (proposed) |
|---|---|---|
| Keycloak DB | WAL archiving + daily snapshots | RPO 5 min, RTO 1 h |
| Keycloak config | Git (realm as code) | Rebuild < 1 h |
| Service databases | WAL archiving + daily snapshots | RPO 5 min, RTO 2 h |
| Object storage | Versioning, cross-region replication | RPO 1 h |
| Valkey | None (sessions and caches are disposable) | Users re-login |
| RabbitMQ | Durable quorum queues | Survives node loss |
| Audit log | Daily copy to immutable storage | RPO ≤ 24 h |

**Single-tenant restore (pooled database).** Point-in-time recovery restores the whole database. To restore one tenant, restore into a scratch instance, extract that tenant's rows by `tenant_id`, and re-import into production in the tenant's context, with the tenant suspended during the operation. This procedure MUST be scripted and rehearsed.

Restore tests at least quarterly; a full DR exercise annually. A backup that has not been restored is assumed broken. Backups contain all tenants and are treated at the highest data class; backup retention is a placeholder (Q-11) and will be defined later.

---

## 32. Local Development

### 32.1 Docker Compose (reference)

```yaml
services:
  keycloak-db:
    image: postgres:16
    environment: { POSTGRES_DB: keycloak, POSTGRES_USER: keycloak, POSTGRES_PASSWORD: change-me }
    volumes: [keycloak-data:/var/lib/postgresql/data]
  keycloak:
    image: quay.io/keycloak/keycloak:26.0        # use the current stable tag (verify)
    command: start-dev --import-realm
    environment:
      KC_DB: postgres
      KC_DB_URL_HOST: keycloak-db
      KC_DB_URL_PORT: 5432
      KC_DB_URL_DATABASE: keycloak
      KC_DB_USERNAME: keycloak
      KC_DB_PASSWORD: change-me
      KC_BOOTSTRAP_ADMIN_USERNAME: admin
      KC_BOOTSTRAP_ADMIN_PASSWORD: admin
      KC_HOSTNAME: auth.excellore.localhost
    ports: ["8080:8080"]
    volumes:
      - ./keycloak/realm-dev.json:/opt/keycloak/data/import/realm-dev.json
      - ./keycloak/themes:/opt/keycloak/themes
    depends_on: [keycloak-db]
  valkey:   { image: "valkey/valkey:8", ports: ["6379:6379"] }
  rabbitmq: { image: "rabbitmq:3-management", ports: ["5672:5672", "15672:15672"] }
  postgres-profile:
    image: postgres:16
    environment: { POSTGRES_DB: profile, POSTGRES_USER: postgres, POSTGRES_PASSWORD: change-me }
    ports: ["5433:5432"]
  postgres-access:
    image: postgres:16
    environment: { POSTGRES_DB: access, POSTGRES_USER: postgres, POSTGRES_PASSWORD: change-me }
    ports: ["5434:5432"]
  objectstore: { image: chrislusf/seaweedfs, command: "server -s3", ports: ["8333:8333"] }
  mailpit:     { image: axllent/mailpit, ports: ["8025:8025", "1025:1025"] }
volumes:
  keycloak-data:
```

A bootstrap script creates the `<app>_migrator` and `<app>_app` roles in each service database ([27.4](#274-database-roles-per-service)) and applies migrations as the migrator.

### 32.2 Local hostnames

```
127.0.0.1  excellore.localhost  profile.excellore.localhost  access.excellore.localhost  auth.excellore.localhost
```

Register matching redirect URIs in the dev realm. `__Host-` cookies need `Secure`: use a local certificate (mkcert) or a dev-only cookie name, never in production.

### 32.3 Seed data

Dev realm: all clients, scopes, mappers, platform roles ([Appendix C](#appendix-c-keycloak-object-inventory)).

**Seed tenants:** `acme` and `globex`, `initech` (suspended, for denial tests).

| User | Memberships and roles | Purpose |
|---|---|---|
| `olivia.owner` | `acme`: owner | Tenant owner flows |
| `adam.admin` | `acme`: admin | Admin flows |
| `mia.member` | `acme`: member; `globex`: viewer | Multi-tenant person |
| `vera.viewer` | `acme`: viewer | Read-only |
| `gina.globex` | `globex`: owner | Isolation tests (no access to `acme`) |
| `nora.nomember` | none | `U0` in isolation tests |
| `penny.platform` | platform-operator | Platform console |
| `sam.support` | platform-support | Support grant flows |
| `sec.platform` | platform-security | Security flows |

Custom role example in `acme`: `billing-manager` with a subset of permissions. All dev-only passwords and secrets are non-production values. Mailpit captures emails.

---
# Part VII: Build Plan

## 33. Build Principles

1. **Vertical slices over layers.** Prove each security-critical path end to end (login → tenant gate → RLS → audit) before widening features.
2. **Foundations first.** Realm, shared libraries, templates, and the Access authority come before the apps that depend on them.
3. **Tests are part of the work.** Isolation, permission, and safeguard tests are written with the code, not after (INV-16).
4. **Small commits.** One step of a work package per commit, each independently reviewable and revertible; use a PR when independent approval is required.
5. **Shared code goes in shared libraries.** Never copy security-sensitive code between apps.
6. **Ask a human for:** real secrets, domains and DNS, legal and compliance choices (Q-05, Q-11), anything in section 0.5 step 4 or 5, and production access.
7. **Never "temporarily" weaken a control.** If a control blocks progress, the control or the design is wrong; raise it.

Order of work: **WP-00 → WP-01 → WP-02 → WP-03 → WP-04 → WP-05 → WP-06 → WP-07 → WP-08 → WP-09 → WP-10 → WP-11 → WP-12 → WP-13 → WP-14 → WP-15.** WP-07 (Shell) and WP-09 (Profile) MAY proceed in parallel after WP-06.

---

## 34. Work Packages

Each package lists: **Goal**, **Depends on**, **Deliverables**, **Key steps**, and **Acceptance criteria (AC)**. A package is done only when every AC is demonstrably true and the definition of done ([0.6](#06-definition-of-done-every-change)) holds.

### WP-00: Repository bootstrap

- **Goal:** A monorepo skeleton that builds, lints, and runs CI on an empty platform.
- **Depends on:** none.
- **Deliverables:** layout from [24.1](#241-layout); `AGENTS.md` ([Appendix J](#appendix-j-agentsmd-template)); `docs/handbook.md` (this book); `docs/adr/`; `CODEOWNERS`; pnpm workspace and Turborepo config; `Excellore.sln`; shared eslint/tsconfig/editorconfig; CI workflow running build, lint, test, secret scan.
- **Key steps:** create empty projects (apps, packages, services, shared libs) with placeholder tests; wire CI; add pre-commit hooks (format, secret scan).
- **AC:** `pnpm -w build && pnpm -w lint && pnpm -w test` pass; `dotnet build && dotnet test` pass; CI green on `main`; `CODEOWNERS` covers the paths in [24.2](#242-branching-and-review).

### WP-01: Local platform stack

- **Goal:** One command starts every dependency.
- **Depends on:** WP-00.
- **Deliverables:** `infra/compose/docker-compose.yml` ([32.1](#321-docker-compose-reference)); `scripts/dev-up.sh` creating `<app>_migrator` and `<app>_app` roles per service database ([27.4](#274-database-roles-per-service)); `.env.example`; local hostnames doc and mkcert instructions.
- **AC:** `scripts/dev-up.sh` brings up Keycloak, Postgres x3, Valkey, RabbitMQ, object store, Mailpit; a script verifies each is healthy; `access_app` and `profile_app` roles exist with `NOBYPASSRLS` and own no tables.

### WP-02: Keycloak realm as code

- **Goal:** The `excellore` realm exactly as in [section 10](#10-keycloak-configuration) and [Appendix C](#appendix-c-keycloak-object-inventory), reproducible from the repository.
- **Depends on:** WP-01.
- **Deliverables:** `infra/keycloak/realm-dev.json` (or OpenTofu module); `infra/keycloak/platform-staff.yaml`; login theme stub `excellore`; `scripts/check-realm.sh` (or a test project) that exercises the realm.
- **Key steps:** realm settings; clients; client scopes and mappers; platform and machine roles; dev users; step-up (ACR-to-LoA) configuration; back-channel logout URLs.
- **AC (all automated in `check-realm`):**
  1. Client-credentials token for `svc-profile-authz` has `aud=api-access` and `roles` containing `access.authz.resolve`.
  2. A user token obtained through the `excellore-profile` flow has `aud=api-profile`, **no** `roles`, **no** `platform_roles`, and **no tenant-related claim**.
  3. A user token for `excellore-access` carries `platform_roles` (empty for normal users, populated for `penny.platform`).
  4. Password grant and implicit flow are rejected for every client.
  5. Redirect URIs are exact; a wildcard or unregistered URI is rejected.
  6. Step-up: requesting the higher ACR forces MFA and the resulting token has the higher `acr`.
  7. No mapper emits all realm roles; Full scope allowed is off on every client.

### WP-03: Shared .NET libraries

- **Goal:** `Excellore.Auth`, `Excellore.Authz`, `Excellore.Platform`, `Excellore.Platform.Testing`, `Excellore.Contracts`.
- **Depends on:** WP-00.
- **Deliverables:** JWT bearer setup ([20.1](#201-authentication)); `ICurrentUser`; step-up handling; `RequirePermission` metadata and `TenantGateFilter` ([20.2](#202-tenant-gate-and-requirepermission-shared-library-excelloreauthz)); `IAuthzResolver` with cache and event invalidation ([20.8](#208-resolver-client-and-cache-excelloreauthz)); `ITenantContext`, `TenantUnitOfWork`, EF Core query filter and `SaveChanges` stamping ([8.4](#84-application-pattern-net-ef-core), [20.3](#203-tenant-unit-of-work-rls-context)); outbox and inbox; RFC 9457 problem helpers; telemetry and health; startup route audit; the **tenant isolation harness** ([25.2](#252-tenant-isolation-test-harness)); event contracts for the catalog in [16.3](#163-event-catalog) (Appendix H).
- **AC:** unit tests cover gate decisions (member, non-member, suspended, missing permission, privileged without MFA); the route-audit test fails on a deliberately unprotected `{tenantId}` route; the RLS meta-test and fixtures run against a Testcontainers Postgres; packages build and pack.

### WP-04: Shared TypeScript packages and templates

- **Goal:** `@excellore/auth`, `@excellore/ui`, `@excellore/config`, and the two templates.
- **Depends on:** WP-02 (for integration tests).
- **Deliverables:** OIDC client, session store, route handlers, `requireSession`, `requireTenant`, Access internal client ([19.7](#197-shared-package-excelloreauth)); `@excellore/ui` header with tenant switcher and app switcher, design tokens; `create-excellore-app` and `dotnet new excellore-service` templates.
- **AC:** integration test against the dev realm: login, callback, refresh with lock, back-channel logout; `requireTenant` returns `404` for a non-member (mocked Access); a generated app starts and logs in; a generated service passes the isolation harness against a sample table.

### WP-05: Access service foundation

- **Goal:** The authority: tenants, membership, catalog, resolve contract, outbox.
- **Depends on:** WP-02, WP-03.
- **Deliverables:** migrations from [Appendix F](#appendix-f-access-database-ddl) with RLS; `permissions.yaml` for Access ([Appendix G](#appendix-g-permission-catalog-initial)); catalog sync endpoint and CI step; system roles; internal contracts `POST /internal/v1/authz/resolve`, `POST /internal/v1/users/tenants:list`, `POST /internal/v1/tenants:resolve-slug`; `identities` JIT upsert; `user_tenant_index` maintenance; platform tenant creation (minimal) with owner invitation placeholder; outbox publisher; `access.authz.changed.v1`.
- **AC:** the effective permission algorithm ([7.5](#75-effective-permission-algorithm)) is unit-tested with a table of cases (including expired assignments, disabled app, suspended tenant, removed member); the isolation harness and DB meta-test pass; catalog sync is idempotent and refuses invalid files; internal endpoints reject user tokens and tokens without `access.authz.resolve`; resolve p95 under the SLO in the local load test.

### WP-06: Access service domain: roles, assignments, groups, invitations, audit

- **Goal:** Everything tenant admins do, with all safeguards.
- **Depends on:** WP-05.
- **Deliverables:** custom roles (create, edit, delete) with S8 and plan limits; assignments with expiry and reasons; groups and group role assignments; invitations (token format with embedded tenant ID, hashed, single-use, expiry, email-equality acceptance); member suspend, reactivate, remove; ownership transfer; step-up enforcement; append-only audit with hash chain; all endpoints in [Appendix I](#appendix-i-api-index) for tenant scope; Keycloak admin client for user creation and execute-actions email (`svc-access-admin`).
- **AC:** tests prove S1 to S12 individually (escalation attempt, self-assignment, privileged without MFA, last owner removal, cross-tenant role use, custom role with `ownerOnly` permission, invitation with wrong email, reused and expired tokens); every endpoint passes the isolation harness; every mutation writes an audit event with `tenant_id`; permission changes propagate to a second API instance within the SLO (event invalidation test).

### WP-07: Shell app

- **Goal:** Sign-in, tenant picker, per-tenant launcher, global sign-out.
- **Depends on:** WP-04, WP-05.
- **Deliverables:** `apps/shell` per [section 13](#13-shell); `app-catalog.yaml`; pending invitations banner.
- **AC:** E2E: sign in, see only the user's tenants, auto-redirect with one tenant, switch tenants, launcher shows only enabled apps the user may open, `/t/unknown` and non-member slugs render the same not-found page, global sign-out ends sessions in all apps.

### WP-08: Access app (tenant console)

- **Goal:** The tenant administration UI.
- **Depends on:** WP-06, WP-04.
- **Deliverables:** `apps/access` tenant console modules in [15.2](#152-modules) (My access, Members, Invitations, Roles with permission picker, Assignments, Apps, Audit, Settings, ownership and deletion); invitation acceptance page (opaque ID from URL query, one-time code entered by invitee); step-up UX.
- **AC:** E2E: owner invites a member, member accepts, owner creates a custom role and assigns it, permissions take effect without re-login, UI hides unavailable actions but API still rejects direct calls; privileged action triggers step-up and succeeds after. **WP-08b (later):** groups UI, access requests.

### WP-09: Profile service and app

- **Goal:** Account-level and member-level profile.
- **Depends on:** WP-04, WP-05 (WP-06 for roles in tests).
- **Deliverables:** `services/profile` per [section 14](#14-profile) (DDL with RLS for `tenant_member_profiles`, `OwnIdentityService`, endpoints, outbox/inbox, `permissions.yaml`); `apps/profile` (account, preferences, security redirects via `kc_action`, sessions, member profile, directory); `svc-profile-authz` integration.
- **AC:** isolation harness passes; `OwnIdentityService` cannot target another user; directory returns only active members of the tenant; `kc_action` allow-list enforced; avatar upload validated and re-encoded; changing a role in Access changes Profile's behavior without re-login.

### WP-10: Workers

- **Goal:** Background processing in tenant context.
- **Depends on:** WP-06, WP-09.
- **Deliverables:** `Excellore.Access.Worker` and `Excellore.Profile.Worker` jobs from [15.6](#156-worker-jobs-worker-access) and [14.6](#146-worker-jobs-worker-profile); tenant-iterating schedule helper; per-tenant concurrency limiter; Mailpit-backed email tests.
- **AC:** expiry removes assignments and support access on time and invalidates caches; jobs run in the right tenant context and cannot read another tenant (test with a deliberately wrong context); handlers are idempotent under duplicate delivery; dead-letter behavior verified.

### WP-11: Platform console and support grants

- **Goal:** Operator tooling and controlled staff access.
- **Depends on:** WP-08.
- **Deliverables:** `/platform/…` route group in the Access app; tenant create, edit, suspend, reactivate, delete, plan and app enablement; support grant request, approval, expiry, owner visibility; platform audit; `platform-staff.yaml` pipeline.
- **AC:** only users with the right `platform_roles` reach platform endpoints; platform roles cannot read tenant data without a grant; a grant is time-boxed and appears in the tenant's audit and to its owner; suspending a tenant denies all its members within the SLO; app enablement immediately hides tiles and neutralizes permissions of the app.

### WP-12: Lifecycle: deletion, export, erasure

- **Goal:** Tenant and user lifecycle is complete and verifiable.
- **Depends on:** WP-10, WP-11.
- **Deliverables:** tenant deletion orchestration with grace period; erasure handlers in every service; confirmations; tombstones; tenant export; user deletion with anonymization; data export for users across tenants.
- **AC:** E2E: a tenant with data in Access and Profile is deleted; after the grace period every service has zero rows for the tenant (verified by a test that connects as the migrator and counts), objects are gone, confirmations recorded, tombstone remains; user deletion anonymizes references and removes global data.

### WP-13: Hardening and end-to-end security

- **Goal:** Production-grade security posture.
- **Depends on:** WP-12.
- **Deliverables:** CSP and security headers verified; rate limits per user and tenant; ZAP and dependency scans in CI; load tests (k6); fault-injection tests; dashboards and alerts from [section 28](#28-observability-and-slos); all scenarios in [25.3](#253-scenarios-that-must-be-automated) automated; threat model entries reviewed.
- **AC:** all 15 scenarios green in CI; no high-severity findings open; alerts fire in a synthetic probing test (many cross-tenant `404`s).

### WP-14: Production infrastructure and runbooks

- **Goal:** Deployable, observable, recoverable production.
- **Depends on:** WP-13.
- **Deliverables:** OpenTofu modules and Kubernetes manifests or Helm charts; HA Keycloak; managed PostgreSQL with PITR; secrets management and rotation jobs; network policies; backup jobs and restore scripts including the **single-tenant restore** procedure; runbooks from [section 30](#30-incident-response-and-runbooks).
- **AC:** staging deployed by pipeline only; restore drill completed and timed; secret rotation drill completed; `/admin`, `master`, and `/internal/**` verified unreachable from the internet; a scheduled check confirms every `tenant_id` table has forced RLS.

### WP-15: Later phase

Self-serve tenant signup (Q-01), access requests and reviews UI, groups UI (WP-08b), per-tenant enterprise SSO through Keycloak Organizations (Q-09), custom domains (Q-10), silo databases (Q-08), tenant security policies (Q-18), billing integration (Q-20). Each needs an RFC and ADR before work starts.

---

# Part VIII: Governance

## 35. Licensing

Verify at adoption and on major upgrades.

| Component | License (as understood) | Note |
|---|---|---|
| Keycloak | Apache 2.0 | CNCF project |
| Next.js, React | MIT | |
| ASP.NET Core, EF Core | MIT | |
| PostgreSQL | PostgreSQL License | |
| Valkey | BSD-3-Clause | Redis changed its license in 2024 |
| RabbitMQ | MPL 2.0 | |
| OpenTofu, OpenBao | MPL 2.0 | Terraform and Vault moved to source-available licenses |
| Wolverine, Rebus | MIT | MassTransit newer majors are commercial **(verify)** |
| Quartz.NET | Apache 2.0 | |
| Hangfire | LGPL (core) | Check fit |
| MediatR, AutoMapper | Commercial in newer versions **(verify)** | Avoid or pin knowingly |
| Duende IdentityModel / AccessTokenManagement | Check each package | Some Duende products are commercial |
| SeaweedFS | Apache 2.0 | |
| MinIO | AGPL-3.0 | Review AGPL obligations |
| Zitadel (alternative IdP) | AGPL-3.0 | Relevant if modified and hosted |

---

## 36. Open Decisions and Defaults

Until a human changes them through [section 37](#37-document-governance), the **default is binding**. Agents MUST implement the default and keep the code ready for the alternative where the cost is small.

| ID | Question | **Binding default** | Alternatives and impact |
|---|---|---|---|
| Q-01 | Self-serve tenant signup and open registration? | **Off.** Platform operators create tenants; people join by invitation | Enable Keycloak registration plus a signup flow in Shell that creates a tenant and makes the person `owner` (WP-15) |
| Q-02 | Custom roles per tenant | **25** (plan feature `maxCustomRoles`) | Per-plan limits |
| Q-03 | Groups | **Data model and API now, UI later** (WP-08b) | Ship UI earlier |
| Q-04 | How Profile changes identity attributes | **`svc-profile-identity` via `OwnIdentityService`** | Keycloak Account REST API with the user's token (smaller blast radius, more complexity) |
| Q-05 | Data residency / regions | **Single region.** `tenants.region` reserved | Regional deployments with tenant routing |
| Q-06 | Tenant deletion grace period | **14 days** | Per plan or legal requirement |
| Q-07 | Authorization data staleness when Access is unreachable | **Reads ≤ 5 min from cache; writes fail closed (`503`)** | Stricter or looser |
| Q-08 | Silo (dedicated) databases | **Not in first release**; keep `ITenantDatabaseResolver` seam | Dedicated DB per large tenant |
| Q-09 | Keycloak Organizations and per-tenant enterprise SSO | **Later phase**; memberships stay in Access | Enterprise login per tenant |
| Q-10 | Custom domains per tenant | **Not in first release** | Wildcard or per-tenant certificates, host-based tenant resolution |
| Q-11 | Compliance scope and retention | **Placeholder. Deferred until the platform is stable. Do not implement retention schedules, archival, or automatic purging. Keep audit data append-only.** | Define retention and compliance later with legal (a future work package) |
| Q-12 | Client authentication for BFFs and workers | **Client secrets, rotated every 90 days** | `private_key_jwt` or mTLS |
| Q-13 | Object storage | **SeaweedFS locally; S3-compatible cloud storage in production** | MinIO (AGPL review) |
| Q-14 | Session store | **Valkey** | PostgreSQL |
| Q-15 | Broker | **RabbitMQ** | Kafka for high-volume streaming |
| Q-16 | Messaging library | **Plain RabbitMQ client behind an `IEventBus` abstraction in `Excellore.Platform`** | Wolverine or Rebus later |
| Q-17 | Hosting | **Kubernetes with Helm or Kustomize; Compose locally** | Simpler container platform |
| Q-18 | Tenant-defined security policies (MFA, session length, allowed login methods) | **Not in first release**; platform baseline applies to all | Per-tenant policies (needs Q-09) |
| Q-19 | Ownership of Access day to day | **Platform team with security oversight** | Dedicated IAM team |
| Q-20 | Billing | **Out of scope; `tenants.plan` and `tenant_apps` are the hooks** | Billing integration (WP-15) |
| Q-21 | Support access model | **Support grants only; no impersonation** | None planned |
| Q-22 | Can a person be a member of unlimited tenants? | **Yes** | Plan limits |
| Q-23 | Authority transport security | **Private network plus machine tokens; mTLS where the platform supports it** | Mesh-enforced mTLS everywhere |

---

## 37. Document Governance

### 37.1 Changing this book

| Change | Process |
|---|---|
| Typo, clarification | Pull request and recorded owner review; an independent reviewer is not required while solo |
| New standard or changed requirement | RFC (short proposal), explicit platform/security owner decision, then a reviewed change; use a PR when independent approval is required |
| **Change to an invariant (INV-nn)** | RFC, explicit security owner decision recorded in the PR, announcement. Invariants change rarely |
| Deviation for one app | ADR with owner and expiry date |
| Architecture-level change | RFC, ADR, security review, announcement |

### 37.2 ADR template

```
# ADR-NNNN: <title>
Status: proposed | accepted | superseded by ADR-XXXX | expired
Date: YYYY-MM-DD
Owner: <name or team, or "agent: needs human review">
Context: forces and constraints (cite invariants and sections)
Decision: what was chosen
Consequences: positive, negative, follow-ups
Expiry / review date: <required for deviations>
```

ADRs live in `docs/adr/`. The decision register ([section 5](#5-decision-register)) lists accepted ADRs that changed the platform.

### 37.3 Versioning and review

Semantic versioning: **major** for changes to decisions, invariants, or mandatory standards; **minor** for new sections or non-breaking additions; **patch** for fixes. Update the change log with every release. Quarterly review by platform and security owners; confirm or correct each **(verify)** item.

### 37.4 Definition of done for a platform change

The handbook or an ADR is updated; Keycloak configuration, infrastructure, and templates are updated in code; tests (including tenant isolation and security cases) cover the change; runbooks and dashboards are updated; owners and on-call know.

---

# Appendices

## Appendix A: Glossary

| Term | Meaning |
|---|---|
| **Tenant** | A customer organization; the unit of isolation and administration |
| **Identity** | A person's global account (Keycloak) |
| **Membership** | Link between an identity and a tenant, with a status |
| **Platform** | Excellore itself, operated by platform staff |
| **Platform staff** | Excellore employees with platform roles |
| **System role** | Role template available in every tenant (`owner`, `admin`, `member`, `viewer`, `auditor`) |
| **Custom role** | Role defined by one tenant |
| **Permission** | Capability key `<app>.<resource>.<action>` declared in code |
| **Permission catalog** | The set of all permissions, synchronized from each service's `permissions.yaml` |
| **App enablement** | Whether a tenant may use an app (platform-controlled) |
| **App access permission** | `<app>.app.access`, lets a member open the app in a tenant |
| **Effective permissions** | The permissions a member actually has in a tenant after all rules |
| **PDP / PEP** | Policy Decision Point (Access authority) and Policy Enforcement Point (each API) |
| **Tenant gate** | The API filter that verifies tenant, membership, and permission before the handler runs |
| **Resolve** | Asking the PDP for the effective permissions of `(user, tenant)` |
| **RLS** | PostgreSQL row-level security |
| **Pooled tenancy** | Tenants share a database and schema; `tenant_id` + RLS isolate them |
| **Silo** | A dedicated database for one tenant |
| **Slug** | URL-safe tenant name used in front-end paths |
| **Support grant** | Time-boxed, audited access for platform staff to one tenant |
| **Step-up** | Asking for stronger or more recent authentication for a sensitive action |
| **Privileged permission** | A permission flagged `privileged` (changes need step-up) |
| **Owner-only permission** | A permission that cannot be placed in custom roles |
| **IdP** | Identity Provider (Keycloak) |
| **OIDC / OAuth 2.0 / PKCE** | Identity and authorization standards; PKCE protects the code flow |
| **BFF** | Backend-for-Frontend; keeps tokens off the browser |
| **Resource server** | An API that accepts access tokens |
| **`aud`, `iss`, `sub`, `sid`, `acr`** | Audience, issuer, subject (user ID), IdP session ID, authentication level |
| **JWKS** | Public keys for verifying token signatures |
| **Client credentials** | OAuth flow for machine identities |
| **Back-channel logout** | IdP-to-app server call that ends app sessions |
| **`kc_action`** | Keycloak parameter that starts an application-initiated action |
| **Outbox / inbox** | Patterns for reliable event publishing and idempotent consumption |
| **Idempotent** | Processing twice has the same effect as once |
| **ADR / RFC** | Architecture Decision Record / proposal document |
| **PII** | Personally identifiable information |

---

## Appendix B: Claims Reference

### B.1 User access token (BFF to API)

| Claim | Example | Used for |
|---|---|---|
| `iss` | `https://auth.excellore.com/realms/excellore` | Validation |
| `aud` | `api-profile` or `api-access` | Validation (INV-06) |
| `sub` | UUID | User ID for the resolver, audit, `ICurrentUser.UserId` |
| `exp`, `iat` | numbers | Validation |
| `azp` | `excellore-profile` | Optional caller check |
| `sid` | UUID | Back-channel logout |
| `acr` | `1` or `2` | Step-up decisions |
| `preferred_username`, `email`, `email_verified`, `name` | strings | Display, identity mirror, invitation matching |
| `platform_roles` | `["platform-operator"]` | **Only in `api-access` tokens**; empty for most users |

**Absent by design:** tenant IDs, tenant roles, tenant permissions, app entitlements (INV-01).

### B.2 ID token (every BFF)

`sub`, `sid`, `acr`, `nonce`, `name`, `given_name`, `family_name`, `email`, `email_verified`, `locale`.

### B.3 Machine token (client credentials)

| Claim | Example |
|---|---|
| `aud` | `api-access` |
| `azp` | `svc-profile-authz` |
| `roles` | `["access.authz.resolve"]` |
| `sub` | Service account ID (not a human) |

---

## Appendix C: Keycloak Object Inventory

### C.1 Clients

| Client ID | Type | Purpose |
|---|---|---|
| `excellore-shell`, `excellore-profile`, `excellore-access` | Confidential, standard flow, PKCE S256 | BFFs |
| `api-profile`, `api-access` | Resource server (audience and machine roles) | APIs |
| `worker-profile`, `worker-access` | Confidential, service account | Workers |
| `svc-shell-authz`, `svc-profile-authz` | Confidential, service account | Calls to Access internal contracts |
| `svc-catalog-sync` | Confidential, service account | CI publishes permission catalogs |
| `svc-access-admin` | Confidential, service account | Keycloak Admin API (users only) |
| `svc-profile-identity` | Confidential, service account | Own-identity update |

### C.2 Client scopes

| Scope | Contents | Assigned to |
|---|---|---|
| `api-profile` | Audience, client-role mapper to `roles` | `excellore-profile`, `worker-profile` |
| `api-access` | Audience, client-role mapper to `roles`, realm-role mapper (`platform-*` only) to `platform_roles` | `excellore-access`, `worker-access`, `svc-shell-authz`, `svc-profile-authz`, `svc-catalog-sync` |

### C.3 Roles

| Role | Kind |
|---|---|
| `platform-admin`, `platform-operator`, `platform-support`, `platform-auditor`, `platform-security` | Realm roles (assigned by code) |
| `access.authz.resolve`, `access.catalog.sync`, `access.worker` | Client roles on `api-access` |
| `profile.worker` | Client role on `api-profile` |

There are **no** human-facing client roles and **no** `app-*` entitlement roles (replaced by tenant permissions, D14 and D18).

---

## Appendix D: Environment Variables

### D.1 BFF (per app)

```
OIDC_ISSUER=https://auth.excellore.com/realms/excellore
OIDC_CLIENT_ID=excellore-profile                 # excellore-shell | excellore-access
OIDC_CLIENT_SECRET=***                           # secrets manager
OIDC_SCOPES="openid profile email api-profile"   # Shell: "openid profile email"
APP_BASE_URL=https://profile.excellore.com
SESSION_COOKIE_NAME=__Host-excellore-profile
SESSION_STORE_URL=redis://valkey:6379
SESSION_KEY_PREFIX=profile:sess:
SESSION_ENCRYPTION_KEY=***
API_BASE_URL=http://api-profile.internal:8080                 # none for Shell
ACCESS_INTERNAL_URL=http://api-access.internal:8080           # internal contracts
AUTHZ_CLIENT_ID=svc-profile-authz                             # svc-shell-authz for Shell
AUTHZ_CLIENT_SECRET=***
STEP_UP_ACR_VALUES=2                                          # Access only
ALLOWED_KC_ACTIONS=UPDATE_PASSWORD,UPDATE_EMAIL,CONFIGURE_TOTP   # Profile only
```

Shell also supplies `PROFILE_ORIGIN` and `ACCESS_ORIGIN` to the shared app catalog loader; the local values are in `.env.example`.

### D.2 API (per service)

```
Auth__Authority=https://auth.excellore.com/realms/excellore
Auth__Audience=api-profile                                    # api-access
ConnectionStrings__Db=Host=…;Database=profile;Username=profile_app;…
ConnectionStrings__Migrator=Host=…;Username=profile_migrator;…   # pipeline only
Authz__AccessInternalUrl=http://api-access.internal:8080     # not set in Access itself
Authz__TokenEndpoint=https://auth.excellore.com/realms/excellore/protocol/openid-connect/token
Authz__ClientId=svc-profile-authz
Authz__ClientSecret=***
Authz__CacheTtlSeconds=60
Authz__MaxStaleSeconds=300
Broker__Url=amqps://…
ObjectStorage__Endpoint=https://…
Keycloak__AdminBaseUrl=https://auth.excellore.com/admin/realms/excellore
Keycloak__AdminClientId=svc-profile-identity                  # svc-access-admin in Access
Keycloak__AdminClientSecret=***
IdentityAdmin__ClientSecret=***                             # Access: svc-access-admin secret
Invitation__AcceptanceOrigin=https://access.excellore.com    # Access: ID-only invitation link and fixed Keycloak redirect
Invitation__SmtpHost=smtp.internal                            # Access invitation delivery
Invitation__SmtpPort=587
Invitation__From=invites@excellore.com
Invitation__SmtpTls=true
Otel__Endpoint=http://otel-collector:4317
```

### D.3 Worker

```
Auth__TokenEndpoint=https://auth.excellore.com/realms/excellore/protocol/openid-connect/token
Auth__ClientId=worker-profile                    # worker-access
Auth__ClientSecret=***
Auth__Scope=api-profile                          # api-access
ConnectionStrings__Db=…
Broker__Url=…
Worker__PerTenantConcurrency=2
```

---

## Appendix E: Naming Conventions

| Thing | Convention | Example |
|---|---|---|
| Host | `<app>.excellore.com` | `profile.excellore.com` |
| Front-end tenant route | `/t/{slug}/…` | `/t/acme/members` |
| API route shapes | `/v1/tenants/{tenantId}/…`, `/v1/me/…`, `/v1/platform/…`, `/internal/v1/…` | `/v1/tenants/{tenantId}/roles` |
| BFF client | `excellore-<app>` | `excellore-access` |
| API client and audience | `api-<app>` | `api-profile` |
| Worker client | `worker-<app>` | `worker-access` |
| Authz service client | `svc-<app>-authz` | `svc-profile-authz` |
| Other service client | `svc-<purpose>` | `svc-access-admin` |
| Platform role | `platform-<name>` | `platform-operator` |
| Machine client role | `<app>.<capability>` | `access.authz.resolve` |
| **Permission key** | `<app>.<resource>.<action>` | `access.role.manage`, `profile.member.read` |
| App access permission | `<app>.app.access` | `profile.app.access` |
| System role | lowercase single word | `owner`, `admin`, `member`, `viewer`, `auditor` |
| Custom role | free text, unique per tenant | `billing-manager` |
| Event type | `<domain>.<entity>.<verb>.v<N>` | `tenant.member.added.v1` |
| Problem type | `urn:excellore:problem:<kebab-name>` | `urn:excellore:problem:step-up-required` |
| .NET projects | `Excellore.<App>.<Layer>` | `Excellore.Access.Api` |
| npm scope | `@excellore/<name>` | `@excellore/auth` |
| Session cookie | `__Host-excellore-<app>` | `__Host-excellore-access` |
| Valkey prefixes | `<app>:sess:`, `authz:{tenantId}:{userId}`, `<app>:t:{tenantId}:…` | `access:sess:` |
| Object keys | `tenants/{tenantId}/…`, `users/{userId}/…` | `tenants/0192…/exports/…` |
| Database | `<app>` | `access` |
| DB roles | `<app>_migrator`, `<app>_app` | `access_app` |
| Postgres settings | `app.tenant_id`, `app.scope` | `set_config('app.tenant_id', …, true)` |
| Env vars | `UPPER_SNAKE` (Node), `Section__Key` (.NET) | `OIDC_ISSUER`, `Auth__Authority` |
| Branch | `wp-<nn>/<description>` | `wp-05/resolve-endpoint` |

### Mapping from v2.0 to v3.0

| v2.0 | v3.0 |
|---|---|
| Realm roles `app-profile`, `app-access`, `excellore-user`; `entitlements` claim | Removed. Replaced by tenant app enablement and `<app>.app.access` permissions |
| Client roles `profile.user`, `profile.admin`, `access.member`, `access.viewer`, `access.manager`, `access.admin`, `access.auditor` | Removed from Keycloak. Replaced by tenant roles and permissions in the Access database |
| `platform-admin` | Kept; joined by `platform-operator`, `platform-support`, `platform-auditor`, `platform-security` |
| `svc-access-admin` with role and group mapping | Narrowed to user operations only |
| Access "admin console for users and roles" | **Tenant console** plus **platform console** |
| Single `profile` data | Global profile (class P) plus per-tenant member profile (class T) |

---

## Appendix F: Access Database DDL

Reference schema for WP-05 and WP-06. Classes in comments: **[P]** platform, **[H]** hybrid, **[T]** tenant. Run migrations as `access_migrator`; the app connects as `access_app`.

```sql
-- ===== Platform tables [P] =====
CREATE TABLE tenants (                       -- [P]
  id uuid PRIMARY KEY,
  slug text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$'),
  name text NOT NULL,
  status text NOT NULL CHECK (status IN ('provisioning','active','suspended','pending_deletion','deleted')),
  plan text NOT NULL DEFAULT 'starter',
  region text NOT NULL DEFAULT 'default',
  authz_version bigint NOT NULL DEFAULT 1,   -- bumped on any change affecting permissions
  deletion_requested_at timestamptz,
  deletion_due_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz
);

CREATE TABLE apps (                          -- [P]
  id text PRIMARY KEY,                       -- 'profile', 'access', …
  name text NOT NULL,
  core boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE permissions (                   -- [P] synced from permissions.yaml
  key text PRIMARY KEY,                      -- '<app>.<resource>.<action>'
  app_id text NOT NULL REFERENCES apps(id),
  description text NOT NULL,
  privileged boolean NOT NULL DEFAULT false,
  owner_only boolean NOT NULL DEFAULT false,
  deprecated boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE identities (                    -- [P] mirror of Keycloak identities; never listed directly
  user_id uuid PRIMARY KEY,                  -- Keycloak sub
  email text NOT NULL,
  email_verified boolean NOT NULL DEFAULT false,
  given_name text, family_name text,
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active','disabled','deleted')),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  last_seen_at timestamptz
);
CREATE UNIQUE INDEX identities_email_uq ON identities (lower(email));

-- Tenant-keyed platform tables: carry tenant_id but are NOT tenant-isolated by RLS.
-- They MUST be listed in infra/db/rls-exempt.txt (CODEOWNERS: security owner).
CREATE TABLE user_tenant_index (             -- [P, exempt] lookup "which tenants is this user in"
  user_id uuid NOT NULL REFERENCES identities(user_id),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  membership_status text NOT NULL,
  PRIMARY KEY (user_id, tenant_id)
);

CREATE TABLE support_grants (                -- [P, exempt] platform staff access metadata only
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  staff_user_id uuid NOT NULL REFERENCES identities(user_id),
  role_id uuid NOT NULL,
  reason text NOT NULL,
  status text NOT NULL CHECK (status IN ('requested','approved','active','expired','revoked','rejected')),
  approved_by uuid,
  starts_at timestamptz, expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE outbox (                        -- [P, exempt] publisher reads without tenant context
  id uuid PRIMARY KEY,
  type text NOT NULL,
  tenant_id uuid,                            -- routing metadata only
  payload jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  published_at timestamptz
);
CREATE TABLE inbox (event_id uuid PRIMARY KEY, received_at timestamptz NOT NULL DEFAULT now());  -- [P]

-- ===== Hybrid tables [H] =====
CREATE TABLE roles (                         -- [H] system rows have tenant_id NULL
  id uuid PRIMARY KEY,
  tenant_id uuid REFERENCES tenants(id),
  name text NOT NULL,
  description text,
  is_system boolean NOT NULL DEFAULT false,
  privileged boolean NOT NULL DEFAULT false, -- derived: contains a privileged permission
  created_by uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((is_system AND tenant_id IS NULL) OR (NOT is_system AND tenant_id IS NOT NULL))
);
CREATE UNIQUE INDEX roles_name_uq
  ON roles (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));

CREATE TABLE role_permissions (              -- [H]
  role_id uuid NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
  tenant_id uuid REFERENCES tenants(id),     -- denormalized from roles for RLS
  permission_key text NOT NULL REFERENCES permissions(key),
  PRIMARY KEY (role_id, permission_key)
);

CREATE TABLE audit_events (                  -- [H] append-only; tenant_id NULL = platform scope
  id uuid PRIMARY KEY,
  tenant_id uuid REFERENCES tenants(id),
  occurred_at timestamptz NOT NULL DEFAULT now(),
  actor_sub uuid, actor_type text NOT NULL CHECK (actor_type IN ('human','service','system')),
  action text NOT NULL,
  target_type text, target_id text,
  before jsonb, after jsonb,
  reason text, acr text, source_ip inet,
  support_grant_id uuid,
  correlation_id uuid,
  prev_hash bytea, hash bytea NOT NULL       -- hash chain per tenant_id scope; serialize with an advisory lock
);
CREATE INDEX audit_events_tenant_time ON audit_events (tenant_id, occurred_at DESC);

-- ===== Tenant tables [T] =====
CREATE TABLE tenant_members (                -- [T]
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  user_id uuid NOT NULL REFERENCES identities(user_id),
  status text NOT NULL CHECK (status IN ('invited','active','suspended','removed')),
  is_support boolean NOT NULL DEFAULT false,
  support_grant_id uuid,
  invited_by uuid,
  joined_at timestamptz,
  expires_at timestamptz,                    -- for support memberships
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, user_id)
);

CREATE TABLE role_assignments (              -- [T]
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL,
  user_id uuid NOT NULL,
  role_id uuid NOT NULL REFERENCES roles(id),
  expires_at timestamptz,
  reason text,
  granted_by uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  revoked_at timestamptz,
  FOREIGN KEY (tenant_id, user_id) REFERENCES tenant_members (tenant_id, user_id)
);
CREATE UNIQUE INDEX role_assignments_active_uq
  ON role_assignments (tenant_id, user_id, role_id) WHERE revoked_at IS NULL;

CREATE TABLE groups (                        -- [T]
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES tenants(id),
  name text NOT NULL, description text,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX groups_name_uq ON groups (tenant_id, lower(name));

CREATE TABLE group_members (                 -- [T]
  tenant_id uuid NOT NULL, group_id uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
  user_id uuid NOT NULL,
  PRIMARY KEY (tenant_id, group_id, user_id),
  FOREIGN KEY (tenant_id, user_id) REFERENCES tenant_members (tenant_id, user_id)
);

CREATE TABLE group_role_assignments (        -- [T]
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  group_id uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
  role_id uuid NOT NULL REFERENCES roles(id),
  expires_at timestamptz, granted_by uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(), revoked_at timestamptz
);

CREATE TABLE tenant_apps (                   -- [T] first gate: which apps the tenant may use
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  app_id text NOT NULL REFERENCES apps(id),
  status text NOT NULL CHECK (status IN ('enabled','disabled')),
  features jsonb NOT NULL DEFAULT '{}',      -- e.g. {"maxMembers":100,"maxCustomRoles":25}
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, app_id)
);

CREATE TABLE tenant_settings (               -- [T]
  tenant_id uuid PRIMARY KEY REFERENCES tenants(id),
  settings jsonb NOT NULL DEFAULT '{}',
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE invitations (                   -- [T]
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  email text NOT NULL,
  role_ids jsonb NOT NULL DEFAULT '[]',
  group_ids jsonb NOT NULL DEFAULT '[]',
  token_hash bytea NOT NULL UNIQUE,          -- SHA-256 of the secret part; token = <tenantId>.<secret>
  status text NOT NULL CHECK (status IN ('pending','accepted','revoked','expired')),
  invited_by uuid NOT NULL,
  expires_at timestamptz NOT NULL,
  accepted_by uuid, accepted_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE access_requests (               -- [T]
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES tenants(id),
  requester_id uuid NOT NULL,
  target_type text NOT NULL CHECK (target_type IN ('role','app')),
  target_key text NOT NULL, reason text NOT NULL, requested_until timestamptz,
  status text NOT NULL CHECK (status IN ('pending','approved','rejected','cancelled','expired')),
  decided_by uuid, decided_at timestamptz, decision_note text,
  created_at timestamptz NOT NULL DEFAULT now()
);

-- ===== RLS: apply to every [T] and [H] table =====
-- Helper expressions:
--   ctx   : NULLIF(current_setting('app.tenant_id', true), '')::uuid
--   scope : NULLIF(current_setting('app.scope',     true), '')
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['tenant_members','role_assignments','groups','group_members',
                           'group_role_assignments','tenant_apps','tenant_settings',
                           'invitations','access_requests'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON %I
        USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
  END LOOP;
END $$;

-- Hybrid: system rows (tenant_id IS NULL) readable by all; writable only in platform scope.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['roles','role_permissions'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY read_rows ON %I FOR SELECT
        USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    EXECUTE format($p$CREATE POLICY write_rows ON %I FOR ALL
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
               OR (tenant_id IS NULL AND NULLIF(current_setting('app.scope', true), '') = 'platform'))
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
               OR (tenant_id IS NULL AND NULLIF(current_setting('app.scope', true), '') = 'platform'))$p$, t);
  END LOOP;
END $$;

-- audit_events: tenant rows by tenant context; platform rows (tenant_id IS NULL) only in platform scope.
ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events FORCE ROW LEVEL SECURITY;
CREATE POLICY audit_select ON audit_events FOR SELECT
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
         OR (tenant_id IS NULL AND NULLIF(current_setting('app.scope', true), '') = 'platform'));
CREATE POLICY audit_insert ON audit_events FOR INSERT
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
         OR (tenant_id IS NULL AND NULLIF(current_setting('app.scope', true), '') = 'platform'));
REVOKE UPDATE, DELETE ON audit_events FROM access_app;      -- append-only
```

Notes for the agent:

- **`app.scope = 'platform'`** is set only by handlers behind `Platform.*` policies and by the catalog sync, never by tenant-scoped paths. It exists solely to touch `tenant_id IS NULL` rows.
- **Resolve** sets `app.tenant_id` to the requested tenant before reading `tenant_members`, `role_assignments`, `tenant_apps`, groups, and roles.
- **Invitation acceptance** parses the tenant ID out of the one-time code in the request body, sets that tenant's context, then looks up the opaque invitation ID and `token_hash`.
- `infra/db/rls-exempt.txt` initially lists: `user_tenant_index`, `support_grants`, `outbox`. The RLS meta-test ([25.2](#252-tenant-isolation-test-harness)) skips exactly these. Adding to the list requires security-owner review.
- Maintain `user_tenant_index` in the same transaction as every membership change.
- Bump `tenants.authz_version` and write an outbox row (`access.authz.changed.v1`) in the same transaction as any change that affects permissions.

---

## Appendix G: Permission Catalog (initial)

`services/access/permissions.yaml`

```yaml
app: access
permissions:
  - { key: access.app.access,          description: Open the Access app in a tenant,            systemRoles: [owner, admin, member, viewer, auditor] }
  - { key: access.member.read,         description: View members of the tenant,                 systemRoles: [owner, admin, viewer, auditor] }
  - { key: access.member.invite,       description: Invite people to the tenant,                systemRoles: [owner, admin] }
  - { key: access.member.suspend,      description: Suspend or reactivate members,             systemRoles: [owner, admin] }
  - { key: access.member.remove,       description: Remove members from the tenant,            systemRoles: [owner, admin] }
  - { key: access.role.read,           description: View roles and the permission catalog,      systemRoles: [owner, admin, viewer, auditor] }
  - { key: access.role.manage,         description: Create, edit, and delete custom roles,      privileged: true, systemRoles: [owner, admin] }
  - { key: access.assignment.manage,   description: Assign and revoke roles,                     systemRoles: [owner, admin] }
  - { key: access.group.manage,        description: Manage groups and group roles,               systemRoles: [owner, admin] }
  - { key: access.apps.read,           description: View apps enabled for the tenant,            systemRoles: [owner, admin, viewer] }
  - { key: access.request.create,      description: Request roles or app access,                 systemRoles: [owner, admin, member] }
  - { key: access.request.approve,     description: Approve or reject access requests,           systemRoles: [owner, admin] }
  - { key: access.audit.read,          description: Read the tenant audit log,                   systemRoles: [owner, admin, auditor] }
  - { key: access.settings.read,       description: View tenant settings,                        systemRoles: [owner, admin] }
  - { key: access.settings.manage,     description: Change tenant settings,                      privileged: true, systemRoles: [owner] }
  - { key: access.supportgrant.manage, description: Approve and revoke platform support access,  privileged: true, ownerOnly: true, systemRoles: [owner] }
  - { key: access.tenant.transfer,     description: Transfer tenant ownership,                   privileged: true, ownerOnly: true, systemRoles: [owner] }
  - { key: access.tenant.delete,       description: Request tenant deletion,                     privileged: true, ownerOnly: true, systemRoles: [owner] }
```

`services/profile/permissions.yaml`

```yaml
app: profile
permissions:
  - { key: profile.app.access,    description: Open Profile in a tenant and edit own member profile, systemRoles: [owner, admin, member, viewer, auditor] }
  - { key: profile.member.read,   description: View the member directory and member profiles,        systemRoles: [owner, admin, member, viewer] }
  - { key: profile.member.manage, description: Edit other members' member profile fields,            systemRoles: [owner, admin] }
```

Notes:

- `systemRoles` list the system roles that include the permission by default. A new permission appears in **no** custom role and in only the listed system roles (INV-17).
- Global Profile features (`/v1/me/…`) need no tenant permission: they act only on the signed-in person.
- `owner` includes every permission of every enabled app; listing it in `systemRoles` keeps the file explicit.
- Removing a permission requires a release with `deprecated: true` first; Access refuses removal while any role contains it.

---

## Appendix H: Event Schemas

All events use the envelope in [16.2](#162-envelope). Only `data` is listed. Schemas live in `Excellore.Contracts` as JSON Schema and C# records.

| Event type | `data` fields |
|---|---|
| `identity.user.created.v1` | `userId` |
| `identity.user.updated.v1` | `userId`, `changed: ["givenName","familyName","locale","email"]` |
| `identity.user.disabled.v1` / `enabled.v1` | `userId` |
| `identity.user.deleted.v1` | `userId` |
| `tenant.created.v1` | `tenantId` |
| `tenant.updated.v1` | `tenantId`, `changed: ["slug","name","plan"]` (field names only) |
| `tenant.suspended.v1` / `reactivated.v1` | `tenantId`, `reasonCode` |
| `tenant.deletion.requested.v1` | `tenantId`, `dueAt` |
| `tenant.deleted.v1` | `tenantId` |
| `tenant.app.enabled.v1` / `disabled.v1` | `tenantId`, `appId` (bounded machine-safe code) |
| `tenant.member.added.v1` | `tenantId`, `userId`, `membershipId` |
| `tenant.member.suspended.v1` / `reactivated.v1` / `removed.v1` | `tenantId`, `userId` |
| `access.authz.changed.v1` | `tenantId`, `userIds?` (absent means whole tenant), `authzVersion` |
| `access.role.created.v1` / `updated.v1` / `deleted.v1` | `tenantId`, `roleId` |
| `access.assignment.granted.v1` / `revoked.v1` | `tenantId`, `userId`, `roleId`, `expiresAt?` |
| `access.supportgrant.created.v1` / `revoked.v1` | `tenantId`, `grantId`, `staffUserId`, `expiresAt` |
| `profile.updated.v1` | `userId`, `changed: ["displayName","timeZone","avatar","preferences"]` (field names only, no values) |
| `profile.deletion.requested.v1` | `userId` |
| `profile.export.ready.v1` | `userId`, `exportId` |
| `access.erasure.confirmed.v1` / `profile.erasure.confirmed.v1` | `scope: "tenant"\|"user"`, `service` (matches event type); tenant scope: `tenantId` matching envelope `tenantid`; user scope: `userId` and no tenant ID |

Rules: no personal data beyond opaque identifiers; `changed` lists field names only, never values; consumers de-duplicate on envelope `id`; unknown fields are ignored (forward compatibility).

---

## Appendix I: API Index

`T` = tenant gate and the listed permission. `A` = authenticated user only (own data). `P` = platform policy. `I` = internal machine policy.

### I.1 Access API (`api-access`)

| Method and path | Gate | Purpose |
|---|---|---|
| `GET /v1/me/tenants` | A | My active tenants |
| `GET /v1/me/invitations` | A | Pending invitations for my verified email |
| `POST /v1/me/invitations/accept` | A | Accept an invitation (`invitationId`, `oneTimeCode` in body) |
| `GET /v1/tenants/{tenantId}/me/access` | T `access.app.access` | My roles, permissions, apps in this tenant |
| `GET /v1/tenants/{tenantId}/members`, `GET …/members/{userId}` | T `access.member.read` | Members |
| `POST …/invitations`, `GET …/invitations`, `DELETE …/invitations/{id}`, `POST …/invitations/{id}/resend` | T `access.member.invite` | Invitations |
| `POST …/members:suspend`, `POST …/members:reactivate` (`userId`, `reason` in body) | T `access.member.suspend` | Suspend, reactivate; no user identifier in the URL (INV-11) |
| `DELETE …/members` (`userId`, `reason` in body) | T `access.member.remove` | Remove; no user identifier in the URL (INV-11) |
| `GET …/roles`, `GET …/roles/{roleId}`, `GET …/permissions` | T `access.role.read` | Roles and permission catalog (enabled apps only) |
| `POST …/roles`, `PUT …/roles/{roleId}`, `DELETE …/roles/{roleId}` | T `access.role.manage` | Custom roles |
| `POST …/role-assignments` (`userId`, `roleId`, optional `expiresAt`, and `reason` in body), `DELETE …/role-assignments/{id}` (`reason` in body) | T `access.assignment.manage` | Assignments; no user identifier in the URL (INV-11) |
| `GET/POST …/groups`, `PUT/DELETE …/groups/{id}/members` (`userId`, `reason` in body), `POST …/groups/{id}/role-assignments` (`roleId`, optional `expiresAt`, `reason` in body), `DELETE …/groups/{id}/role-assignments/{assignmentId}` (`reason` in body) | T `access.group.manage` | Groups; no user identifier in the URL (INV-11) |
| `GET …/apps` | T `access.apps.read` | Enabled apps |
| `POST …/access-requests` | T `access.request.create` | Request access |
| `GET …/access-requests`, `POST …/access-requests/{id}/approve`, `…/reject` | T `access.request.approve` | Review requests |
| `GET …/audit-events`, `POST …/audit-events/exports` | T `access.audit.read` | Audit |
| `GET …/settings` / `PATCH …/settings` | T `access.settings.read` / `access.settings.manage` | Settings |
| `GET …/support-grants`, `POST …/support-grants/{id}/approve`, `…/revoke` | T `access.supportgrant.manage` | Support grants |
| `POST …/ownership-transfers`, `POST …/ownership-transfers/{id}/accept` | T `access.tenant.transfer` / target member | Ownership transfer |
| `POST …/deletion-requests` | T `access.tenant.delete` | Tenant deletion |
| `POST /v1/platform/tenants`, `GET /v1/platform/tenants`, `GET/PATCH /v1/platform/tenants/{id}` | P operator | Tenant management |
| `POST /v1/platform/tenants/{id}/owner-invitation:resend` | P operator | Rotate and resend a pending provisioning owner invitation |
| `POST /v1/platform/tenants/{id}/suspend`, `…/reactivate`, `…/deletion-requests` | P operator (suspend also security) | Lifecycle |
| `PUT /v1/platform/tenants/{id}/apps/{appId}` | P operator | Enable or disable an app |
| `GET /v1/platform/permissions` | P any platform role | Catalog |
| `GET /v1/platform/audit-events` | P auditor | Platform audit |
| `POST /v1/platform/support-requests` | P support | Request a grant |
| `POST /v1/platform/users/{id}/sign-out`, `…/disable` | P security | Emergency actions |
| `POST /internal/v1/authz/resolve` | I `access.authz.resolve` | Resolve `(user, tenant)` |
| `POST /internal/v1/users/tenants:list` | I `access.authz.resolve` | User's tenants |
| `POST /internal/v1/tenants:resolve-slug` | I `access.authz.resolve` | Slug to ID for a member |
| `PUT /internal/v1/catalog/{app}` | I `access.catalog.sync` | Publish a permission catalog |

### I.2 Profile API (`api-profile`)

See [14.4](#144-api-audience-api-profile).

---

## Appendix J: AGENTS.md Template

Place this at the repository root as `AGENTS.md`. It points an agent at this book and restates the rules that matter most.

````markdown
# AGENTS.md: Excellore

You are working on **Excellore**, a multi-tenant SaaS platform (Shell, Profile, Access).
The single source of truth is `docs/handbook.md`. Read **Part 0 (Agent Operating Manual)** first,
then the sections relevant to your task. If anything here conflicts with the handbook, the handbook wins.

## Non-negotiables (full list: handbook section 0.3)
- Tokens are tenant-agnostic. Never put tenant, role, or permission data in Keycloak tokens (INV-01).
- Tenant authorization comes only from the Access authority, per request (INV-02). Never trust a header, body, or token for tenant.
- Every `{tenantId}` route uses the tenant gate and a `RequirePermission` (INV-04).
- Every tenant table has `tenant_id` + forced RLS; runtime DB roles never bypass it (INV-03).
- Browser never gets tokens (INV-05). One audience per API (INV-06).
- Workers/messages carry `tenantId`; never tokens (INV-07).
- Fail closed; non-members get 404 (INV-10). No secrets/PII in logs (INV-11).
- Audit security-relevant changes (INV-12). Everything is code (INV-14).
- Every new tenant endpoint/table ships with an isolation test (INV-16).

## How to work
1. Pick the next unfinished work package in handbook section 34 (in order). State which one.
2. Implement in small, reviewable commits on `main`; use a PR when independent approval is required. Use the templates and shared libraries.
3. Run: `pnpm -w build && pnpm -w lint && pnpm -w test` and `dotnet build && dotnet test`.
4. Meet the acceptance criteria of the package and the definition of done (handbook 0.6).
5. If the handbook is silent: follow section 0.5 and the binding defaults in section 36.
   Security- or data-model-affecting assumptions go in `docs/adr/` as proposed ADRs.

## Never
- Weaken or skip a test, header, or check to make a build pass.
- Add dependencies without checking the license list (handbook section 35).
- Touch production, real secrets, or shared environments.
- Resolve a conflict with an invariant silently. Stop and ask.

## Names and conventions
See handbook Appendix E. Permission keys: `<app>.<resource>.<action>`. Events: `<domain>.<entity>.<verb>.v<N>`.
````

---

*End of the Excellore Platform Handbook, version 5.1.*
