import { randomBytes } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const app = process.argv[2];
if (!['shell', 'profile', 'access'].includes(app))
  throw new Error(
    'Usage: node scripts/setup-next-local.mjs <shell|profile|access>',
  );
const envPath = resolve('.env');
const input = await readFile(envPath, 'utf8');
const env = Object.fromEntries(
  input
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => [
      line.slice(0, line.indexOf('=')),
      line.slice(line.indexOf('=') + 1),
    ]),
);
const upper = app.toUpperCase();
const secureBrowserOrigin =
  new URL(env[`${upper}_ORIGIN`]).protocol === 'https:';
const existingPath = resolve('apps', app, '.env.local');
let key;
try {
  const existing = await readFile(existingPath, 'utf8');
  key = existing.match(/^SESSION_ENCRYPTION_KEY=([A-Za-z0-9_-]{43})$/m)?.[1];
} catch (error) {
  if (error.code !== 'ENOENT') throw error;
}
key ??= randomBytes(32).toString('base64url');
const values = {
  OIDC_ISSUER: `${env.KEYCLOAK_ORIGIN}/realms/excellore`,
  OIDC_CLIENT_ID: `excellore-${app}`,
  OIDC_CLIENT_SECRET: env[`${upper}_BFF_SECRET`],
  OIDC_SCOPES:
    app === 'shell'
      ? 'openid profile email'
      : `openid profile email api-${app}`,
  APP_BASE_URL: env[`${upper}_ORIGIN`],
  SESSION_COOKIE_NAME: secureBrowserOrigin
    ? `__Host-excellore-${app}`
    : `excellore-dev-${app}`,
  SESSION_STORE_URL: 'redis://localhost:6379',
  SESSION_KEY_PREFIX: `${app}:sess:`,
  SESSION_ENCRYPTION_KEY: key,
  EXCELLORE_INSECURE_LOCAL_DEV: 'true',
  ACCESS_INTERNAL_URL: 'http://localhost:5002',
  AUTHZ_CLIENT_ID: `svc-${app}-authz`,
  AUTHZ_CLIENT_SECRET: env[`SVC_${upper}_AUTHZ_SECRET`],
  API_BASE_URL:
    app === 'access' ? 'http://localhost:5002' : 'http://localhost:5001',
  SHELL_ORIGIN: env.SHELL_ORIGIN,
  PROFILE_ORIGIN: env.PROFILE_ORIGIN,
  ACCESS_ORIGIN: env.ACCESS_ORIGIN,
};
if (Object.values(values).some((value) => !value || value === '__GENERATE__'))
  throw new Error('Local .env is incomplete; run scripts/dev-up.sh');
await writeFile(
  existingPath,
  Object.entries(values)
    .map(([name, value]) => `${name}=${value}`)
    .join('\n') + '\n',
  { mode: 0o600 },
);
process.stdout.write(`Prepared ignored apps/${app}/.env.local.\n`);
