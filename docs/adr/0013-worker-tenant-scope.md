# ADR-0013: Worker tenant scope

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

Handbook sections 7.10, 8.4, and 21.4 require background jobs to run as machine identities in a tenant context. The HTTP tenant context contains a user authorization snapshot, which a worker does not have. Reusing it would suggest that a message's tenant ID is a user permission decision. INV-03, INV-07, and INV-10 apply.

## Decision

`Excellore.Platform` provides `TenantWorkerScope`. Each call creates a fresh dependency injection scope, binds a nonempty tenant ID to its scoped `ITenantIdAccessor` before resolving the job service, passes cancellation to the job, and disposes the scope on completion or failure. A replaced tenant accessor fails before the job starts. The tenant ID in an event or schedule selects the RLS context; it does not authorize a user. The worker host authenticates with its machine identity, and the job rechecks ownership of referenced resources inside that context. Database work uses `TenantUnitOfWork` to set PostgreSQL's transaction-local RLS setting.

## Consequences

Worker hosts call `AddExcelloreWorkerTenantScope()` and register each job as a scoped service. They invoke `RunAsync<TJob, TResult>` once per message or tenant iteration and do not retain a job service across calls. HTTP hosts continue to use the authorization-backed tenant gate. This change provides the scope primitive; worker consumers and their cross-tenant data tests remain WP-10 work.
