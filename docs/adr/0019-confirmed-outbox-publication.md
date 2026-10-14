# ADR-0019: Confirmed outbox publication

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

Handbook sections 16.4 and 21.3 require an outbox publisher, at-least-once delivery, and idempotent consumers. ADR-0018 stores events transactionally, but a worker still needs to send them to RabbitMQ and mark them published only after broker acceptance. The source table is class P and RLS exempt because it carries tenant routing metadata; service migrations must add it to `infra/db/rls-exempt.txt`. INV-03, INV-07, INV-09, INV-11, and INV-16 apply.

## Decision

`IEventBus.CreatePublisherAsync` creates a long-lived publisher with RabbitMQ publisher confirms and confirmation tracking. It declares the durable `excellore.events` topic exchange. Each message is persistent, carries its event ID and type in AMQP properties, and uses mandatory routing. A successful `PublishAsync` means the broker confirmed delivery to at least one bound queue. A negative acknowledgement or unroutable return throws; no event is marked published. Producers must provision the intended service queues and bindings before sending events. The publisher serializes calls on its channel, and callers dispose it on shutdown.

`PostgresOutboxDispatcher.DispatchOnceAsync` selects one pending row with `FOR UPDATE SKIP LOCKED` in a database transaction, revalidates its envelope metadata against the row, publishes through `IEventPublisher`, and sets `published_at` only after successful confirmation. A broker failure rolls back the transaction, leaving the row pending. A broker confirmation followed by a database or connection failure can produce a duplicate on retry; consumers deduplicate by event ID using their inbox. The dispatcher processes one row per call so worker hosts can control polling, retry delay, and shutdown. A service migration grants a dedicated publisher role SELECT and UPDATE of `published_at`, without permission to change payload or type. The shared library does not create tables or start a background worker.

## Consequences

The database transaction holds one row lock while awaiting broker confirmation. Multiple publishers can process distinct rows, and a locked row does not block later rows. Mandatory routing leaves a row pending when no queue is bound; that is preferable to marking an undelivered event published and needs an operational alert before production. A malformed stored row likewise remains pending for investigation; neither case is silently discarded. Tests cover broker confirmation and unroutable returns, database rollback and duplicate retry after uncertain confirmation, restricted grants, metadata revalidation, and `SKIP LOCKED` concurrency. No event body, credential, or exception detail is logged by these components.

RabbitMQ's [.NET publisher-confirm guide](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet) describes tracked confirmations and returned-message failures; its [reliability guide](https://www.rabbitmq.com/docs/reliability) explains duplicate delivery after an uncertain confirmation.
