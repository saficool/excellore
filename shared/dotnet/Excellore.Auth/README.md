# Excellore.Auth

Shared JWT bearer and current-user setup for .NET APIs. API hosts call `AddExcelloreAuthentication(configuration, "api-profile")` or use `"api-access"`, then add `app.UseAuthentication()` and `app.UseAuthorization()` before protected endpoints.

Set `Auth:Authority` to the Keycloak `excellore` realm issuer. HTTPS metadata is required by default. Local development may pass `requireHttpsMetadata: false` for the HTTP realm in the local stack; production must keep the default.

The package registers `ICurrentUser` and the `Internal.*` and `Platform.*` policies in handbook section 20.1. Tenant permissions are resolved separately by `Excellore.Authz`. `HasRecentMfa` checks for `acr=2` as described in proposed ADR-0005.
