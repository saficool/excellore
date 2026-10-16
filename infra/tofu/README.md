# OpenTofu deployment

The `excellore-platform` module creates a namespace under Kubernetes Restricted Pod Security and
installs the reviewed local Helm chart atomically. The staging root accepts a short-lived
kubeconfig and a pipeline-rendered values file. Neither input is committed or persisted in this
directory.

State must use the selected cloud's encrypted, versioned remote backend with locking. Local state,
plan files, and generated values are deployment artifacts and are ignored by Git.

The cloud adapter remains intentionally separate because the cloud provider has not been selected.
It must provide:

- a private Kubernetes cluster and workload identity;
- managed PostgreSQL for Keycloak, Access, and Profile with five-minute-or-better PITR;
- managed Valkey, RabbitMQ quorum queues, private object storage with versioning, and SMTP;
- private endpoints/CIDRs, DNS, TLS, a secret manager, and an External Secrets `ClusterSecretStore`;
- encrypted remote OpenTofu state with locking and pipeline-only write access.

After the provider decision, add `infra/tofu/modules/<provider>-platform` and a production root.
Do not add public database endpoints or long-lived cloud credentials to this module.
