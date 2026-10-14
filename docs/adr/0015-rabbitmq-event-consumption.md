# ADR-0015: RabbitMQ platform event consumption

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

Handbook sections 16.1, 16.4, 20.8, and 21.3 require at-least-once RabbitMQ delivery, service queues, idempotent handlers, retries, and a dead-letter path. Q-16 chooses the plain RabbitMQ client behind `IEventBus` in `Excellore.Platform`. Authorization-change delivery must start after the Valkey subscriber and acknowledge only after the shared handler succeeds. INV-02, INV-07, INV-09, INV-10, INV-11, and INV-16 apply.

## Decision

`RabbitMqEventBus` implements the consumer side of `IEventBus` using `RabbitMQ.Client` 7.2.2. It declares the durable topic exchange `excellore.events`, a durable quorum queue `excellore.<service>.events`, and a durable quorum dead-letter queue. Exact event-type bindings avoid consuming unrelated messages. The source queue uses at-least-once dead lettering and reject-publish overflow. Delivery bodies larger than 256 KiB or with an unregistered routing key go directly to the dead-letter queue. Handlers receive a copied body because the RabbitMQ client owns the delivery buffer.

The transport processes one delivery at a time. A successful handler is acknowledged. Other failures get two bounded in-process retries (200 ms and 1 s), then are rejected to the dead-letter queue. A delivery interrupted by shutdown remains unacknowledged and can be redelivered. The transport does not log payloads, credentials, or exception messages. Its subscription owns the connection and channel, which the client automatically recovers after network failures. The app passes its broker URI from configuration; this library stores no real URI or credentials.

`RabbitMqAuthzChangedConsumer` subscribes the service queue to `access.authz.changed.v1` and calls the existing contract-validating, idempotent handler. `AddRabbitMqAuthzChangedConsumer` requires `AddValkeyAuthzResolver` first, so the hosted Valkey subscriber starts before broker consumption. The starter APIs do not enable these registrations yet. Additional event types for a service must join the same service queue and dispatcher, rather than create competing consumers with separate handler sets. The confirmed publisher and outbox dispatcher are described in ADR-0019.

## Consequences

RabbitMQ and Valkey integration tests prove success acknowledgement, bounded retry and dead lettering, and broker-to-cache invalidation. The dead-letter queue needs backlog alerts and a replay runbook before production. The durable inbox primitive for service database handlers is in ADR-0016; this authorization handler treats repeated event IDs idempotently. `RabbitMQ.Client` 7.2.2 is Apache-2.0 OR MPL-2.0 licensed; `Testcontainers.RabbitMq` 4.16.0 is MIT licensed, checked against their NuGet listings at adoption.
