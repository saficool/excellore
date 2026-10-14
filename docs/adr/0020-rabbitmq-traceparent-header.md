# ADR-0020: RabbitMQ trace context header

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

Handbook sections 21.3 and 28.1 require `traceparent` propagation through the broker. The event envelope in section 16.2 has no trace field, and the outbox publisher runs after the originating API request has ended. INV-02, INV-07, INV-11, and INV-15 require trace context to remain observability metadata, never tenant authority or a place for personal data.

## Decision

`RabbitMqEventBus` creates a producer activity while publishing and puts its W3C `traceparent` in one AMQP message header. If no producer activity is recorded, it may forward the current W3C activity ID. It does not copy `tracestate`, baggage, token claims, tenant IDs, or request headers. The consumer accepts a bounded, valid `traceparent` header and starts a consumer activity parented to it around handler attempts. A missing or malformed header starts an independent trace and does not change message acknowledgement or event validation. The trace header is never used for authorization or tenant selection.

This step covers broker transport. The transactional outbox currently stores no originating trace context, so the dispatcher cannot preserve the API-to-publisher parent across the asynchronous database boundary. That needs a separate durable-metadata decision and migration before end-to-end API-to-worker traces can be claimed.

## Consequences

Broker publishers and consumers can correlate work when an activity is active at publish time. The event body and JSON Schema stay unchanged. The header adds only a fixed-size trace identifier; no application values are copied. Tests cover a broker round trip, the header contents, malformed input, and unchanged delivery behavior. The publisher and consumer activities require a host OpenTelemetry listener/exporter to be recorded.

The [W3C Trace Context specification](https://www.w3.org/TR/trace-context/) defines `traceparent` and its privacy constraints. The [RabbitMQ .NET client guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) documents custom AMQP headers.
