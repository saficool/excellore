# ADR-0057: Tenant API rate limit at the shared gate

Status: Proposed

## Context

WP-06 requires per-user and per-tenant request rate limits. Applying them within Access handlers would leave other tenant endpoints without a common boundary.

## Decision

The shared tenant route extension applies fixed-window limits of 60 requests per minute per user and 600 per minute per tenant after authorization. A limit returns HTTP 429 without calling the handler. The application boundary is per API instance; WP-14 adds a deployment-wide ingress quota when replicas are deployed.

## Consequences

The application limits bound requests per API instance and export an anonymous rejection counter. WP-14 adds the deployment-wide ingress quota across replicas. A request is still subject to both its user and tenant partition inside every replica. INV-02, INV-04, INV-10, INV-11, and INV-16 apply.
