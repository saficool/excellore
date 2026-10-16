# ADR-0020: Cloud-specific production adapter

- Status: Proposed
- Date: 2026-10-16

## Context

WP-14 requires managed PostgreSQL with point-in-time recovery, private Kubernetes networking,
object storage, secrets management, DNS, and backup drills. The handbook does not select AWS,
Azure, Google Cloud, or another provider. Selecting one affects cost, security boundaries,
operating procedures, and public contracts.

## Proposed decision

Keep the application deployment provider-neutral: a reviewed Helm chart installed through a small
OpenTofu module. Add one cloud-specific adapter only after the owner chooses a provider and region.
The adapter must expose private service endpoints to the chart, enforce five-minute-or-better
database PITR, use workload identity, synchronize separate per-workload secrets, and keep all state
in an encrypted remote backend with locking. It must also enforce service-mesh mTLS for pod traffic;
the chart's private Keycloak HTTP authority is accepted only for its exact cluster-service host.

No production or shared environment is created by this ADR. The staging and production roots stay
incomplete until the provider, region, domains, secret manager, and remote-state backend are
approved.

## Consequences

- Application manifests, network policy, and deployment validation can be completed and tested
  without choosing a cloud.
- Managed database, DNS, certificate, object-storage replication, and actual restore/rotation
  drills wait for the provider decision and real staging credentials.
- A provider choice cannot be made silently or embedded as an accidental platform dependency.

## Invariants

INV-03, INV-11, INV-14, and INV-18.
