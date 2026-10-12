# Excellore.Authz

Shared tenant route gate for .NET minimal APIs. Register `AddExcelloreAuthz()`, an `IAuthzResolver`, and the app's code-first `IPrivilegeClassifier`. Add the gate to a route group, then declare a permission on every tenant endpoint:

```csharp
var tenant = app.MapGroup("/v1/tenants/{tenantId:guid}").AddTenantGate();
tenant.MapGet("/members", ListMembers).RequirePermission("profile.member.read");
app.AssertTenantRoutesProtected();
```

Call `AssertTenantRoutesProtected()` after mapping all routes and before starting the app. It rejects tenant routes missing authentication, the gate, or permission metadata. Use `AllowAnyMember()` only for an endpoint intentionally available to every active tenant member.

The gate reads the user from the validated JWT and the tenant ID from the route. It resolves Access authorization before the handler, checks every required permission, and sets a scoped tenant context for the handler. The context is cleared afterward. A denied or suspended membership returns `404`; a missing permission or missing MFA returns `403`; authority unavailability returns `503`. All errors use RFC 9457 problem details.

The default privilege classifier requires step-up for every permission until replaced by an app catalog. See proposed ADR-0006.

`AddCachedAuthzResolver()` registers a singleton `IAuthzResolver` and `IAuthzCacheInvalidator`. Register a singleton-safe `IAuthzSnapshotSource` to fetch a fresh Access decision. The in-memory cache keeps an immutable snapshot for at most 10 seconds per `(tenantId, userId)`. Call `Invalidate(tenantId, userIds)` for an authorization-change event; omit `userIds` to clear the tenant. An empty or malformed user list also clears the tenant. The resolver discards a fetch if invalidation arrives before it completes. It never serves an expired snapshot when the source is unavailable.

Call `AddAccessAuthzClient(configuration)` before `AddCachedAuthzResolver()` to supply the fresh Access source. Configure `Authz:AccessInternalUrl` as an origin URL, `Authz:TokenEndpoint` as the realm's HTTPS token endpoint, and `Authz:ClientId` and `Authz:ClientSecret` from the secret store. The source posts `(userId, tenantId)` to `/internal/v1/authz/resolve` with a cached OAuth client-credentials machine token. A non-200, malformed, or cross-tenant response fails closed. The Access URL may use HTTP on the private service network. For a local HTTP Keycloak endpoint only, pass `allowInsecureTokenEndpointForLocalDevelopment: true`. See proposed ADR-0008.

`AddCachedAuthzResolver()` also registers `AccessAuthzChangedHandler`. Pass the raw `access.authz.changed.v1` event JSON to `Handle` after broker delivery; it validates the shared contract, invalidates only the named tenant and users, and ignores recent duplicate event IDs. An absent, null, or empty `userIds` list invalidates the whole tenant. The contract and JSON Schema are in `Excellore.Contracts`. See proposed ADR-0009.

The Valkey layer, RabbitMQ subscription, and database transaction context are later WP-03 slices. See proposed ADR-0007 for the interim cache behavior.
