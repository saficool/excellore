# ADR-0057: Tenant API rate limit at the shared gate

Status: Proposed

## Context

WP-06 requires per-user and per-tenant request rate limits. Applying them within Access handlers would leave other tenant endpoints without a common boundary.

## Decision

The shared tenant route extension applies fixed-window limits of 60 requests per minute per user and 600 per minute per tenant after authorization. A limit returns HTTP 429 without calling the handler. The first implementation is per API instance; distributed quota coordination belongs to WP-13.

## Consequences

The current limits bound requests per instance, so a client can consume more across several replicas. Metrics and deployment-level ingress limits should cover that gap until WP-13. INV-02, INV-04, INV-10, and INV-16 apply.
