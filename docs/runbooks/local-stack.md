# Local platform stack (WP-01)

## Start and verify

Install Docker Desktop and start its engine. On Windows, run these commands in Git Bash; on Linux or macOS, use Bash. OpenSSL and curl must be on `PATH`.

```sh
bash scripts/dev-up.sh
bash scripts/check-dev-stack.sh
mkcert -install
pnpm local:https:setup
pnpm local:web
```

The first run creates an ignored `.env` with random local credentials and an ignored SeaweedFS S3 configuration. Keep both local. The script starts Keycloak, its PostgreSQL database, separate Access and Profile PostgreSQL databases, Valkey, RabbitMQ, SeaweedFS, and Mailpit. It also creates the `<app>_migrator`, `<app>_app`, and `<app>_publisher` roles in each service database. Re-running is safe. The app and publisher roles own no objects and cannot bypass RLS (INV-03). Migrations start in later work packages.

WP-02 also renders `infra/keycloak/realm-dev.json` with the generated credentials into ignored `.local/keycloak/realm-dev.json`. Keycloak imports that realm on first startup. Changes to the source realm do not alter an existing local realm automatically. To discard only the local `excellore` realm and re-import it from the current file, run:

```sh
node scripts/render-dev-realm.mjs
EXCELLORE_DEV_REALM_RESET=1 node scripts/reset-dev-realm.mjs
docker compose --env-file .env -f infra/compose/docker-compose.yml restart keycloak
bash scripts/check-realm.sh
```

The reset deletes local seed users, sessions, and events in the `excellore` realm. It does not remove the `master` realm or other stack data. `check-realm.sh` verifies client grants and redirects, token claims, browser login, and OTP step-up. It may wait up to 31 seconds for a fresh OTP time window. Login and admin events stay in Keycloak's event store; they are not forwarded to logs (ADR-0004).

The endpoints bind to `127.0.0.1` only:

| Service               | Local endpoint                              |
| --------------------- | ------------------------------------------- |
| Keycloak (browser)    | `https://auth.excellore.localhost`          |
| Keycloak (host admin) | `http://auth.excellore.localhost:8080`      |
| Keycloak readiness    | `http://127.0.0.1:9000/health/ready`        |
| Access PostgreSQL     | `127.0.0.1:5434`                            |
| Profile PostgreSQL    | `127.0.0.1:5433`                            |
| Valkey                | `127.0.0.1:6379`                            |
| RabbitMQ / management | `127.0.0.1:5672` / `http://127.0.0.1:15672` |
| SeaweedFS S3          | `http://127.0.0.1:8333`                     |
| Mailpit web / SMTP    | `http://127.0.0.1:8025` / `127.0.0.1:1025`  |

The Keycloak bootstrap admin username is `admin`; its password is in your local `.env`. RabbitMQ and SeaweedFS credentials are there too. Do not use these credentials outside this local stack.

## Hostnames

`*.localhost` names normally resolve to the local machine without a hosts-file entry. On this Windows development machine, `auth.excellore.localhost` resolves to `127.0.0.1` and reaches Keycloak. Check the name before changing your hosts file:

```powershell
Resolve-DnsName auth.excellore.localhost -Type A
curl.exe --fail --silent --show-error --output NUL --write-out '%{http_code}' https://auth.excellore.localhost/realms/master/.well-known/openid-configuration
```

If the name does not resolve on your machine, add this line to the Windows hosts file (`C:\Windows\System32\drivers\etc\hosts`, edit as Administrator), or `/etc/hosts` on Linux or macOS:

```text
127.0.0.1  excellore.localhost  profile.excellore.localhost  access.excellore.localhost  auth.excellore.localhost
```

## Local HTTPS for browser flows

Install [mkcert](https://github.com/FiloSottile/mkcert#installation), then trust its machine-local CA once:

```sh
mkcert -install
pnpm local:https:setup
```

The setup command creates an ignored certificate for all four Excellore hostnames, switches the ignored local configuration to HTTPS, prepares every BFF, updates Keycloak when Docker is running, and reconciles the existing realm's redirect and logout URLs. It never prints credentials. The certificate and private key remain in ignored `.local/certs`; keep the mkcert private CA key on this machine and never commit or share it.

Start the HTTPS gateway and all three Next.js apps in one terminal:

```sh
pnpm local:web
```

The gateway binds only to `127.0.0.1:443`, accepts only the four exact local hostnames, replaces forwarded headers, and proxies to the loopback-only HTTP development ports. It supports Next.js hot reload. Keycloak reuses the active MFA SSO session when moving between apps; an explicit privileged step-up still forces fresh authentication. Node acceptance scripts need the local CA explicitly; run them through `node scripts/with-local-ca.mjs <script>`.

## Stop

```sh
docker compose --env-file .env -f infra/compose/docker-compose.yml down
```

This keeps named database and broker volumes. Removing those volumes deletes local data.

## Profile BFF login (WP-04)

After `scripts/dev-up.sh`, `pnpm local:https:setup`, and `pnpm local:web`, open the Profile app:

```sh
start https://profile.excellore.localhost/account
```

Choose **Sign in** and use the local `olivia.owner` account. Its password is `DEV_USER_PASSWORD` in ignored `.env`; the current OTP comes from the `DEV_OTP_SECRET` seed. The login completes at `/account`. The browser receives an opaque secure session cookie; tokens stay in Valkey.

For WP-05 Access database work, run `bash scripts/migrate-access.sh` after `scripts/dev-up.sh`. It applies the reviewed schema, registers the Access and Profile apps, installs the membership index trigger, installs the append-only audit guard and custom-role archive column, and grants `access_publisher` only outbox read and publication-state update rights; reruns are safe. Run it before starting an updated Access API, because role reads use the archive column. Configure the Access API with `Auth__Authority` and `ConnectionStrings__Access` (the `access_app` connection). A catalog deployment uses a `svc-catalog-sync` machine token with the `api-access` audience and `access.catalog.sync` client role; never use a browser token. The three internal authorization and tenant lookup routes require a `svc-*-authz` machine token with the `access.authz.resolve` role.

The Access outbox worker requires `ConnectionStrings__AccessPublisher` for the `access_publisher` login and `Messaging__RabbitMqUri` for RabbitMQ. Use the generated credentials in ignored `.env` and keep them out of command arguments and logs. The worker retries pending events until the broker confirms routing to a bound consumer queue. The current local stack does not provision a consumer queue for `access.authz.changed.v1`; that event stays pending until a consumer starts. The disposable integration test verifies publication and retry without changing the local databases.

For WP-10, rerun `bash scripts/dev-up.sh` to provision the scoped worker logins and reconcile their read-only Keycloak directory roles, then rerun both migration scripts. Start `node scripts/run-worker-local.mjs access` and `node scripts/run-worker-local.mjs profile` in separate terminals. The Access worker publishes its outbox, sweeps expired role assignments and support grants once per minute, and checks Keycloak identity drift at startup and daily. The Profile worker publishes global Profile events, projects Access membership events, and refreshes existing name and locale mirrors from Keycloak after identity update events. Directory scans use numeric pagination and fail closed above 10,000 users; person IDs and email never enter their request URLs or logs. Keep the Access and Profile APIs running separately. Worker credentials come from ignored `.env`; the launch script does not print them. The Profile export request remains pending while cross-service export and erasure are finalized in WP-12.

The Access worker also builds pending tenant audit exports in the same tenant sweep. The status route returns `ready` after the private object is stored; download through `/api/t/{slug}/audit-events/exports/{exportId}/download` in the Access BFF with recent MFA. The download is capped at 64 MiB; oversized jobs become `failed` and are audited. The local launcher supplies the ignored object-store credentials and uses the separate `excellore-audit-exports` bucket.

Bulk role assignment is an owner-only Access API job. Submit 1–100 active tenant member IDs and one nonprivileged role with a safe reason code to `POST /v1/tenants/{tenantId}/role-assignment-batches`; poll `GET /v1/tenants/{tenantId}/role-assignment-batches/{batchId}` for aggregate progress. The Access worker processes each target atomically in tenant context. Recent MFA is required when queueing; no person ID enters a URL.

WP-06 tenant reads are available at `/v1/tenants/{tenantId}/me/access`, `/roles`, `/roles/{roleId}`, and `/permissions` on the Access API. They require an `api-access` user token; the tenant gate checks current membership and the endpoint permission before any handler runs. The role and permission readers use the gated tenant context for RLS. The Access HTTP integration test uses a disposable database and test authentication scheme; it does not require a running local Keycloak instance.

WP-06 custom-role mutations are `POST /v1/tenants/{tenantId}/roles`, `PUT /v1/tenants/{tenantId}/roles/{roleId}`, and `DELETE /v1/tenants/{tenantId}/roles/{roleId}`. They require `access.role.manage` and recent MFA. Create and update bodies contain `name` (3–60 characters), optional `description`, and `permissions` (an array of catalog keys). Owner-only or deprecated permissions and permissions of disabled apps are rejected; a caller cannot grant permissions they do not hold. Deletion is refused while an active direct or group assignment uses the role. Changes write a versioned audit record, increment tenant authorization version, and queue authorization and role events. The HTTP integration test runs against a disposable PostgreSQL database; no manual Keycloak setup is needed for that test.

WP-06 direct assignments use `POST /v1/tenants/{tenantId}/role-assignments` with JSON `userId`, `roleId`, optional `expiresAt`, and required machine-safe `reason` code (for example `support-access`). Revoke with `DELETE /v1/tenants/{tenantId}/role-assignments/{id}` and JSON `{ "reason": "access-ended" }`. They require `access.assignment.manage`; privileged roles also require recent MFA. The ordinary endpoints refuse owner-role changes and self-assignment. The Access integration test exercises grant, revoke, expiry replacement, tenant boundaries, events, and audit against disposable PostgreSQL. No manual testing is required for this group.

WP-06 groups use `GET/POST /v1/tenants/{tenantId}/groups`, `PUT/DELETE /v1/tenants/{tenantId}/groups/{groupId}/members` with JSON `{ "userId": "...", "reason": "group-change" }`, and `POST /v1/tenants/{tenantId}/groups/{groupId}/role-assignments` with `roleId`, optional `expiresAt`, and `reason`. Revoke a group role through `DELETE /v1/tenants/{tenantId}/groups/{groupId}/role-assignments/{assignmentId}` with a reason body. They require `access.group.manage`. Apply `bash scripts/migrate-access.sh` before starting the updated API; migration 007 adds group-role live-grant uniqueness. The HTTP integration test uses disposable PostgreSQL and needs no manual group setup.

The generated tenant route and API proxy call the Access authority. The Access resolver now serves these contracts, but a newly provisioned tenant remains unavailable until its owner accepts the invitation. Access now sends an ID-only invitation link and a one-time code to local Mailpit (`http://127.0.0.1:8025`). A new Keycloak identity also receives a required-actions email there. The Access acceptance page and code entry arrive in WP-08; until then, backend acceptance is covered by automated tests. This first UI checks the login and server-side session. To verify the route wiring automatically after the app starts, run `node scripts/check-profile-login.mjs`. The script uses the local seed credentials in ignored `.env` and prints no credentials.

## Shell app (WP-07)

Start the migrated Access API on `http://localhost:5002`, then run the shared web command:

```sh
pnpm local:web
```

Open `https://excellore.localhost`. Shell signs in through Keycloak and stores its tokens in Valkey. `/` lists active memberships from Access; one membership redirects to `/t/{slug}`. The launcher shows only enabled apps backed by the member's app-access permission. The Shell's pending invitation banner reads Access's machine-protected internal contract. Its acceptance link opens the Access acceptance page delivered in WP-08; the one-time code is never in the link. Sign out from the shared header invalidates all BFF sessions through the shared Valkey marker and ends the Keycloak SSO session; verified backchannel logout remains active. A browser's last-used tenant preference is checked against the fresh tenant list before redirecting.

For the automated Shell UI smoke test, leave port 5002 free and run `node scripts/with-local-ca.mjs scripts/check-shell-login.mjs`. The script uses local Keycloak and Valkey, serves a temporary Access contract stub on port 5002, and checks sign-in, tenant and app visibility, single-tenant redirect, unknown tenant handling, and cross-app sign-out. It prints no credentials.

## Access tenant console (WP-08)

After `scripts/dev-up.sh` and the Access migrations, use separate terminals from the repository root:

```sh
pnpm exec node scripts/run-access-local.mjs
pnpm exec node scripts/sync-dev-catalog.mjs
pnpm exec node scripts/seed-dev-access.mjs
pnpm local:web
```

Run the first command before catalog sync and seed; leave it running. The seed uses local Keycloak users and Mailpit to activate the `acme` owner through the Access API. It is safe to rerun when the owner is already active. No invitation code or token is printed. Open `https://access.excellore.localhost/t/acme/my-access` and sign in as the development `olivia.owner` user. The password is `DEV_USER_PASSWORD` in ignored `.env`; the OTP is generated from `DEV_OTP_SECRET` (the ignored `.local/show-dev-otp.mjs` helper can display the current code). The browser holds only the opaque Access session cookie.

With the API and BFF running, `node scripts/with-local-ca.mjs scripts/check-access-console.mjs` verifies real login, tenant isolation, read proxies, CSRF rejection, and that no tokens are rendered in the page. On its first run it invites the local `nora.nomember` identity, reads the one-time code from Mailpit without printing it, accepts the invitation, then checks custom role creation, assignment, and permission changes in the same browser session. It revokes the test assignment and deletes the test role; Nora remains a member for repeat runs. The Assignments read requires MFA because `access.assignment.manage` is privileged. The local Access BFF has its own `svc-access-authz` machine client; `scripts/dev-up.sh` reconciles it and Keycloak's `basic` subject scope for existing development realms.

## Platform console (WP-11)

With the Access API and BFF running as above, open `https://access.excellore.localhost/platform` and sign in as the local `penny.platform` fixture. Use `DEV_USER_PASSWORD` and the OTP helper described above. The console lists tenant metadata, app status, numeric feature limits, permission catalog, system roles, and the code-owned staff roster. The operator can create or edit a tenant, suspend or reactivate it, enable or disable non-core apps, change numeric feature limits, and request deletion with a reason code. The owner invitation is delivered through local Mailpit. `sam.support` can request an owner-approved, time-boxed read-only support grant; `sec.platform` can suspend a tenant. The auditor view is restricted to `platform-auditor` or `platform-admin` roles. These assignments are development fixtures from `infra/keycloak/platform-staff.yaml`, validated by CI. No emergency user sign-out or disable control is exposed pending an INV-11-compliant Keycloak design.

The automated `PlatformTenantApiTests` use disposable PostgreSQL to exercise role gates, RLS, app authorization invalidation, lifecycle, and audit. The shared BFF tests verify the platform proxy keeps access tokens server-side and enforces CSRF. With the local stack running, `node scripts/check-platform-console.mjs` verifies a real operator login, platform page, role gate, metadata read, and CSRF rejection without printing credentials. No manual test is required for WP-11 verification.

## Profile account and member UI (WP-09)

Start the local stack and Access API, then run from the repository root:

```sh
bash scripts/migrate-profile.sh
pnpm exec node scripts/sync-dev-catalog.mjs
pnpm exec node scripts/run-profile-local.mjs
pnpm local:web
```

Leave the Profile API and shared web command running in separate terminals. Open `https://profile.excellore.localhost/account` and sign in with the local development user. Account details, preferences, Keycloak security actions, sessions, avatar, export request, and deletion request are available there. Personal-data export requests remain pending until the complete cross-service flow in WP-12. The tenant page at `/t/acme` offers the member profile and active visible directory; the Access seed must have activated the `acme` tenant and owner first. Admin edits require `profile.member.manage` and a reason. The browser resizes supported avatar images up to 10 MB to a 512-pixel WebP before upload; the API still validates the upload against its 700 KB limit and re-encodes it before object storage. Upload progress and validation errors appear in the Avatar card. Processed images are served through the authenticated `/api/avatar` BFF route. The browser receives no object-store credential or token.

Profile API configuration is loaded by `scripts/run-profile-local.mjs` from ignored local credentials. Its `api-profile` bearer token gate uses `svc-profile-authz` to resolve tenant permissions in Access and consumes Access invalidation events through RabbitMQ. Run `dotnet test services/profile/Excellore.Profile.Tests` for the disposable PostgreSQL isolation, outbox, and avatar processing tests.
With the Profile API and BFF running, `node scripts/with-local-ca.mjs scripts/check-profile-login.mjs` exercises OIDC login, authenticated Profile reads, tenant membership and directory, avatar processing and cleanup, CSP, and logout. It uses the ignored local development account and prints no credentials.
