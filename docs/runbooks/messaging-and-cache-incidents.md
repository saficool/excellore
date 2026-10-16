# Messaging and cache incidents

## DLQ growth

1. Identify event type and failure class using message IDs and schema versions only. Do not log or
   paste payloads.
2. Stop the affected consumer when retries amplify harm. Verify schema compatibility, handler
   idempotency, tenant context, and database availability.
3. Deploy the fix, replay a bounded sample, verify inbox deduplication, then drain at a controlled
   rate. Never edit payloads in place.

## RabbitMQ outage

APIs continue committing their outbox transaction but publication stops. Restore quorum and TLS,
then watch publisher confirmation, outbox age, consumer lag, and DLQ count. Do not mark outbox rows
published without broker confirmation.

## Valkey outage

BFF sessions and distributed authorization cache become unavailable. Keep writes fail closed;
reads may use in-process permission cache only within the five-minute authority-outage window.
Recover Valkey rather than restoring it from backup, rotate session key prefixes when integrity is
uncertain, and require fresh login.
