# `create-excellore-app`

Generate a new Next.js BFF inside the monorepo:

```sh
node packages/create-excellore-app/bin/create-excellore-app.mjs reports 3003
pnpm install
```

The command requires an app ID and local port. It creates `apps/<id>` and refuses to overwrite existing files. `--refresh` updates an app intentionally generated from the current template; review its diff before committing. The app requires a matching Keycloak confidential client, its API audience, a machine client for Access internal contracts, app catalog registration, and deployment environment values before tenant pages work. Those items are configured in later work packages for the first-party apps.

For the existing Profile sample, start the local stack, run `node scripts/setup-next-local.mjs profile`, then `pnpm --filter @excellore/profile dev`. `node scripts/check-profile-login.mjs` exercises the real OIDC login, session, CSP, and logout routes.
