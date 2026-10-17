# Excellore Kubernetes chart

This chart deploys the Shell, Profile, Access, and Platform BFFs; the Profile, Access, and Notifications
APIs and workers; an HA Keycloak pair; and a narrow public proxy for Keycloak. Every service is a private ClusterIP.
Only the four BFFs and the identity proxy have ingress resources.

## Required deployment inputs

Override every application image digest, all `*.example.invalid` values, and each private endpoint
CIDR before deployment. `identityIngressCidrs` must contain the private address range reached when
workloads resolve the public issuer hostname. The deployment pipeline rejects all-zero placeholder
digests.

The identity proxy is the only public Keycloak route. Internal services reach the private Keycloak
service, while Keycloak keeps its public issuer fixed and dynamically resolves backchannel URLs.
The selected cloud adapter must provide service-mesh mTLS for these in-cluster HTTP connections.

The chart expects a `ClusterSecretStore` and creates one `ExternalSecret` per secret-bearing
workload. The remote records must expose environment-variable keys expected by that workload.
At minimum:

- Shell, Profile, and Access BFF records: `OIDC_CLIENT_SECRET`, `SESSION_ENCRYPTION_KEY`,
  `AUTHZ_CLIENT_SECRET`. The Platform BFF has no authorization machine client and needs only the
  first two values.
- Profile API: `ConnectionStrings__Profile`, `Authz__ClientSecret`, `Authz__RabbitMq`, and object
  store credentials/configuration.
- Access API: `ConnectionStrings__Access`, `IdentityAdmin__ClientSecret`, `Email__SmtpPassword`, and
  object store configuration. Access uses SMTP only for the synchronous one-time invitation code.
- Workers: publisher/worker connection strings, RabbitMQ, object store configuration, machine
  actor ID where required, and Keycloak directory client credentials.
- Notifications API: `ConnectionStrings__Notifications`.
- Notifications worker: `ConnectionStrings__NotificationsWorker`, `Messaging__RabbitMqUri`,
  `Authz__ClientSecret`, and `Email__SmtpPassword`.
- Keycloak: `KC_DB_URL`, `KC_DB_USERNAME`, `KC_DB_PASSWORD`, and the BFF and machine-client
  secrets referenced by the generated realm, including `PLATFORM_BFF_SECRET`,
  `SVC_NOTIFICATIONS_AUTHZ_SECRET`, and `KEYCLOAK_EMAIL_SMTP_PASSWORD`. Bootstrap
  administrator credentials belong only in the one-time realm provisioning job.

The private CIDR lists enable only the documented PostgreSQL, Valkey, RabbitMQ, object store, and
SMTP ports. Empty lists intentionally leave those connections blocked.

## Render and validate

```bash
helm template excellore infra/k8s/excellore --namespace excellore \
  --output-dir .local/production-manifests
pnpm -w infra:check -- .local/production-manifests
```

The check rejects public API or worker routes, plaintext Secret resources, root containers,
writable root filesystems, shared runtime Secrets, and a missing default-deny policy. It also
checks that the public identity edge blocks Keycloak admin, the master realm, and internal paths.
Pass `--deployment` after the rendered path in the protected pipeline to also reject placeholder
domains, placeholder digests, and every image that is not pinned by SHA-256 digest.
