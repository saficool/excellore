# RFC-0001: Minimize global identity events

Status: accepted by platform owner @saficool on 2026-10-14

## Problem

Handbook Appendix H required `email` and `emailVerified` in `identity.user.created.v1`, while INV-11 prohibits personal data in events. Section 16 already says events carry identifiers. The conflicting Appendix H exception could cause email to enter RabbitMQ, inboxes, and logs.

## Decision

The owner chose to omit email. `identity.user.created.v1` carries only an opaque `userId`. Other identity lifecycle events carry only `userId`; `identity.user.updated.v1` may add names of changed fields, never their values. Global identity events have no tenant ID. No invariant is changed.

## Consequences

Consumers needing current email must use an authorized identity or profile flow rather than the event payload. Producers and consumers must reject known personal-data fields in these events. Handbook version 4.2 removes the email exception from Appendix H.
