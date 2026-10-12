# Local platform stack (WP-01)

## Start and verify

Install Docker Desktop and start its engine. On Windows, run these commands in Git Bash; on Linux or macOS, use Bash. OpenSSL and curl must be on `PATH`.

```sh
bash scripts/dev-up.sh
bash scripts/check-dev-stack.sh
```

The first run creates an ignored `.env` with random local credentials and an ignored SeaweedFS S3 configuration. Keep both local. The script starts Keycloak, its PostgreSQL database, separate Access and Profile PostgreSQL databases, Valkey, RabbitMQ, SeaweedFS, and Mailpit. It also creates the `<app>_migrator` and `<app>_app` roles in each service database. Re-running is safe. The runtime roles own no objects and cannot bypass RLS (INV-03). Migrations start in later work packages.

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

## Hostnames and local HTTPS

Add this line to the Windows hosts file (`C:\Windows\System32\drivers\etc\hosts`, edit as Administrator), or `/etc/hosts` on Linux or macOS:

```text
127.0.0.1  excellore.localhost  profile.excellore.localhost  access.excellore.localhost  auth.excellore.localhost
```

Install [mkcert](https://github.com/FiloSottile/mkcert#installation), then run:

```sh
mkcert -install
mkdir -p .local/certs
mkcert -cert-file .local/certs/excellore.pem -key-file .local/certs/excellore-key.pem \
  excellore.localhost profile.excellore.localhost access.excellore.localhost auth.excellore.localhost
```

The certificates stay in ignored `.local/certs`. WP-01 serves Keycloak over local HTTP; wire these certificates into a local HTTPS proxy before testing `__Host-` cookies and OIDC browser flows (WP-02/WP-04). Register the exact HTTPS redirect URLs in the development realm when that realm is added.

## Stop

```sh
docker compose --env-file .env -f infra/compose/docker-compose.yml down
```

This keeps named database and broker volumes. Removing those volumes deletes local data.
