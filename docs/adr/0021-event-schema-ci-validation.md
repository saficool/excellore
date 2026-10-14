# ADR-0021: Event schema validation in CI

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

Handbook sections 16.3, 16.4, and 25.1 require event schemas in `Excellore.Contracts` to be validated in CI. The existing `access.authz.changed.v1` schema was embedded and inspected by a .NET test, but no JSON Schema validator compiled it or checked example events. A broken schema could therefore pass CI. INV-07, INV-11, INV-15, and INV-16 apply.

## Decision

The `contracts:check` command compiles every `*.schema.json` file with Ajv's JSON Schema Draft 2020-12 validator and standard format checks. Each schema has an example of the same event type. CI requires the example to pass; malformed IDs, types, and missing data to fail; and unknown fields to remain accepted for forward compatibility. The examples use synthetic identifiers and no personal data. Ajv and ajv-formats are pinned MIT-licensed development dependencies. The existing C# event parser and consumer idempotency tests remain separate checks.

## Consequences

Adding a schema now requires an example and passes through a real JSON Schema validator in local checks and CI. The check does not prove that arbitrary producer output conforms to the schema; each producer must test its serialized events, and every new consumer still needs an idempotency test. Schema validation and C# parsing may differ on constraints that JSON Schema cannot express, such as equality between envelope and data tenant IDs.
