# Production deployment and drills

## Purpose

Deploy Excellore only through the protected GitHub environment after the cloud adapter is selected.
No operator applies manifests, edits Keycloak, or changes database grants by hand.

## Preconditions

1. CI is green for the exact commit.
2. Images are built from that commit, scanned, signed, and addressed by digest.
3. The provider pipeline has created private managed services, DNS, certificates, the External
   Secrets store, and encrypted remote OpenTofu state with locking.
4. Pipeline-rendered Helm values contain no `.example.invalid`, all-zero digest, public data-store
   endpoint, or plaintext secret.
5. A database backup is current and the previous release digests are recorded.

## Deployment

1. Authenticate through workload identity and obtain a short-lived cluster credential.
2. Run `tofu init`, `tofu fmt -check -recursive`, `tofu validate`, and save a reviewed plan.
3. Apply that exact plan from the protected environment. The Helm release is atomic and waits for
   readiness.
4. Run `scripts/operations/verify-public-boundary.sh` from outside the private network.
5. Confirm all BFF and API readiness probes, Keycloak cluster/database health, RabbitMQ consumers,
   outbox age, error rate, and audit-chain alerts.
6. Record commit, image digests, plan checksum, actor, start/end time, and verification result.

## Rollback

Roll back application digests through a new reviewed plan. Never roll a database schema backward.
If a forward-compatible migration is implicated, stop affected writes, deploy the prior application
images, and prepare a forward repair migration.

## Required drills

- Quarterly: full database restore and scripted single-tenant extraction/reimport, recording RPO
  and RTO. Validate the provider's metadata-only evidence with
  `scripts/operations/verify-restore-drill.sh`.
- Quarterly: rotate one non-production BFF secret and one database credential, then prove old
  credentials fail. Validate the provider's metadata-only evidence with
  `scripts/operations/verify-secret-rotation.sh`.
- Annually: region-level recovery exercise after a provider adapter defines the secondary target.

Staging deployment, restore, and rotation acceptance evidence remains incomplete until the owner
selects a cloud provider/region and supplies protected staging access.
