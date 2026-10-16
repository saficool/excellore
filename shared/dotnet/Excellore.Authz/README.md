# Excellore.Authz

Shared tenant route gate for .NET minimal APIs. Register `AddExcelloreAuthz()`, an `IAuthzResolver`, and the app's code-first `IPrivilegeClassifier`. Add the gate to a route group, then declare a permission on every tenant endpoint:

```csharp
var tenant = app.MapGroup("/v1/tenants/{tenantId:guid}").AddTenantGate();
tenant.MapGet("/members", ListMembers).RequirePermission("profile.member.read");
app.AssertTenantRoutesProtected();
```

Call `AssertTenantRoutesProtected()` after mapping all routes and before starting the app. It rejects tenant routes missing authentication, the gate, or permission metadata. Use `AllowAnyMember()` only for an endpoint intentionally available to every active tenant member.

Chain `AddTenantRateLimit()` after `AddTenantGate()` on every tenant route group. The shared fixed-window policy allows 60 requests per minute per user and 600 per minute per tenant on each API instance and returns RFC 9457 `429` before handler code. WP-14 adds the deployment-wide ingress quota for replicated services.

The gate reads the user from the validated JWT and the tenant ID from the route. It resolves Access authorization before the handler, checks every required permission, and sets a scoped tenant context for the handler. The context is cleared afterward. A denied or suspended membership returns `404`; a missing permission or missing MFA returns `403`; authority unavailability returns `503`. All errors use RFC 9457 problem details.

Repeated tenant-gate `404` decisions are tracked per actor only in bounded process memory. The `Excellore.Authz` meter exports anonymous denial, probing-alert, and rate-limit counters without user IDs, IP addresses, or untrusted tenant IDs. Register that meter with the host's OpenTelemetry provider in WP-14.

After a successful gate decision, the gate tags the HTTP request activity and opens a structured log scope with the validated opaque `tenant.id` for handler logs. Denied requests are never attributed to a tenant from their untrusted route. The log scope and tenant context close when the handler returns or throws. Hosts must collect ASP.NET Core request activities and enable logging scopes for these attributes to appear in exported telemetry.

The default privilege classifier requires step-up for every permission until replaced by an app catalog. A service with a live catalog may implement `IsPrivilegedAsync` to read the current privilege flag; unknown permissions still require step-up and a failed lookup must fail closed. See proposed ADR-0006 and ADR-0043.

`AddCachedAuthzResolver()` registers a singleton `IAuthzResolver` and `IAuthzCacheInvalidator`. Register a singleton-safe `IAuthzSnapshotSource` to fetch a fresh Access decision. The in-memory cache keeps an immutable snapshot for at most 10 seconds per `(tenantId, userId)`. Call `InvalidateAsync(tenantId, userIds)` for an authorization-change event; omit `userIds` to clear the tenant. An empty or malformed user list also clears the tenant. The resolver discards a fetch if invalidation arrives before it completes. It never serves an expired snapshot when the source is unavailable.

Call `AddAccessAuthzClient(configuration)` before `AddCachedAuthzResolver()` to supply the fresh Access source. Configure `Authz:AccessInternalUrl` as an origin URL, `Authz:TokenEndpoint` as the realm's HTTPS token endpoint, and `Authz:ClientId` and `Authz:ClientSecret` from the secret store. The source posts `(userId, tenantId)` to `/internal/v1/authz/resolve` with a cached OAuth client-credentials machine token. A non-200, malformed, or cross-tenant response fails closed. The Access URL may use HTTP on the private service network. For a local HTTP Keycloak endpoint only, pass `allowInsecureTokenEndpointForLocalDevelopment: true`. See proposed ADR-0008.

`AddCachedAuthzResolver()` also registers `AccessAuthzChangedHandler`. Pass the raw `access.authz.changed.v1` event JSON to `HandleAsync` after broker delivery; it validates the shared contract, invalidates only the named tenant and users, and ignores recent duplicate event IDs. A failed invalidation leaves the event retryable. An absent, null, or empty `userIds` list invalidates the whole tenant. The contract and JSON Schema are in `Excellore.Contracts`. See proposed ADR-0009.

`ValkeyAuthzCache` provides the 60-second distributed snapshot primitive. `ValkeyAuthzSnapshotSource` reads it before Access, stores fresh decisions only when the generation is unchanged, and uses a fresh Access decision if Valkey fails. `InvalidateAsync` changes only the named user's generation or the whole tenant's generation. Prefix keys with the app name. `ValkeyAuthzInvalidationFanout` subscribes each replica to an app-scoped Valkey channel, updates the distributed generation, then publishes only the tenant ID to clear every replica's in-memory entries for that tenant. Start the subscriber before broker consumption; a failed publish keeps the event retryable. Missed pub/sub delivery falls back to the 10-second in-memory TTL. See proposed ADR-0014.

`AddValkeyAuthzResolver(configuration, appPrefix)` assembles the Access HTTP source, Valkey snapshot, in-memory resolver, event handler, and one hosted fanout subscriber. Register a singleton `IConnectionMultiplexer` from the app's configuration first. The extension validates the app prefix and Access configuration, and `AddCachedAuthzResolver()` or `AddAccessAuthzClient()` called again will not replace the Valkey path. Then call `AddRabbitMqAuthzChangedConsumer(appPrefix, brokerUri)` with a broker URI from configuration. It adds the `access.authz.changed.v1` consumer after the fanout subscriber, so the broker acknowledges only when `AccessAuthzChangedHandler.HandleAsync` succeeds. The starter APIs do not call these registrations yet. See proposed ADR-0007 for fail-closed resolver behavior and ADR-0015 for RabbitMQ delivery.
