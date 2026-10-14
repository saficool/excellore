# RFC-0005: Global Profile event scope

Status: accepted by platform owner @saficool on 2026-10-14

## Problem

Profile account details, data exports, and account deletion requests are global user flows under handbook section 14. Appendix H lists no tenant ID for their events, but section 16.2 previously said only “global identity events” omit envelope `tenantid`.

## Decision

The owner chose to classify `profile.updated.v1`, `profile.deletion.requested.v1`, and `profile.export.ready.v1` as global user events. Their envelopes omit `tenantid`; the handbook now describes global user events explicitly. This preserves INV-07 because these events carry no tenant work or tenant-scoped data.

## Consequences

Consumers process these events without tenant context and must never use them to authorize tenant work. Future tenant member profile changes need separate tenant-scoped events carrying `tenantid`.
