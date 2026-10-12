# ADR-0004: Keep Keycloak events in its event store

Status: Accepted by repository owner on 2026-10-12.
Date: 2026-10-12

## Context

The handbook called for login and admin events to be shipped to logs. Keycloak's default event logger can include user identifiers, while INV-11 forbids personal data in logs. The owner chose to keep events in Keycloak only until a redacting export path is available.

## Decision

Enable login and admin events in the Keycloak event store. Do not enable the default event logging listener. A future listener may export events only after it is designed and tested to redact personal data, tokens, and secrets. Record that design in a separate ADR before enabling export.

## Consequences

Keycloak remains the source for login and admin event investigation. Central log search and alerting over those events are deferred. Operational access to the event store must remain restricted to authorized staff.
