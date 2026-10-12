# Local platform stack (WP-01)

## Start and verify

Install Docker Desktop and start its engine. On Windows, run these commands in Git Bash; on Linux or macOS, use Bash. OpenSSL and curl must be on `PATH`.

```sh
bash scripts/dev-up.sh
bash scripts/check-dev-stack.sh
```

The first run creates an ignored `.env` with random local credentials and an ignored SeaweedFS S3 configuration. Keep both local. The script starts Keycloak, its PostgreSQL database, separate Access and Profile PostgreSQL databases, Valkey, RabbitMQ, SeaweedFS, and Mailpit. It also creates the `<app>_migrator` and `<app>_app` roles in each service database. Re-running is safe. The runtime roles own no objects and cannot bypass RLS (INV-03). Migrations start in later work packages.

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
| Keycloak              | `http://auth.excellore.localhost:8080`      |
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
curl.exe --fail --silent --show-error --output NUL --write-out '%{http_code}' http://auth.excellore.localhost:8080/realms/master/.well-known/openid-configuration
```

If the name does not resolve on your machine, add this line to the Windows hosts file (`C:\Windows\System32\drivers\etc\hosts`, edit as Administrator), or `/etc/hosts` on Linux or macOS:

```text
127.0.0.1  excellore.localhost  profile.excellore.localhost  access.excellore.localhost  auth.excellore.localhost
```

## Local HTTPS for later browser flows

WP-01 serves Keycloak over HTTP. No certificate is needed to start or verify this stack. Before testing `__Host-` cookies and OIDC browser flows in WP-02/WP-04, install [mkcert](https://github.com/FiloSottile/mkcert#installation) and generate a certificate:

```sh
mkcert -install
mkdir -p .local/certs
mkcert -cert-file .local/certs/excellore.pem -key-file .local/certs/excellore-key.pem \
  excellore.localhost profile.excellore.localhost access.excellore.localhost auth.excellore.localhost
```

The certificates stay in ignored `.local/certs`. `mkcert -install` adds a local CA to your machine's trust store; keep its private CA key on your machine and never commit or share it. The certificate does not turn on HTTPS by itself. Connect it to a local HTTPS proxy when the browser flows are built, then register the exact HTTPS redirect URLs in the development realm. Node.js does not automatically trust the system CA; set `NODE_EXTRA_CA_CERTS` to the `rootCA.pem` file reported by `mkcert -CAROOT` when a Node process calls a locally secured endpoint.

## Stop

```sh
docker compose --env-file .env -f infra/compose/docker-compose.yml down
```

This keeps named database and broker volumes. Removing those volumes deletes local data.
