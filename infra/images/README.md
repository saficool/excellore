# Production images

These Dockerfiles produce the immutable application images used by the Kubernetes manifests.
They do not copy local environment files or credentials into an image, and their runtime stages
run as fixed non-root users.

## Next.js BFFs

Build one BFF by setting `APP_NAME` to `shell`, `profile`, or `access`:

```bash
docker build -f infra/images/next/Dockerfile \
  --build-arg APP_NAME=shell -t excellore-shell:local .
```

The shared recipe emits a Next.js standalone server. It includes only the code traced for the
selected app, static assets, and the public app catalog. Configuration and secrets must be injected
at runtime.

## .NET services and workers

Build one project by passing its repository-relative project path:

```bash
docker build -f infra/images/dotnet/Dockerfile \
  --build-arg PROJECT=services/access/Excellore.Access.Api/Excellore.Access.Api.csproj \
  -t excellore-access-api:local .
```

The image entrypoint is `dotnet`; the deployment supplies the published assembly name as its
argument, for example `Excellore.Access.Api.dll`. API containers listen on port 8080. Workers do
not expose a port.

## Keycloak

Generate the production realm and build the optimized Keycloak image:

```bash
node infra/keycloak/build-realm.mjs --production
docker build -f infra/images/keycloak/Dockerfile -t excellore-keycloak:local .
```

The production realm contains clients, scopes, flows, and service accounts, with client secrets
resolved from runtime environment variables during the first import. It contains no development
people, passwords, or OTP seeds. Subsequent realm changes require the deployment pipeline's realm
reconciliation step; Keycloak import does not overwrite an existing realm.
