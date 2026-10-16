import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { parse } from 'yaml';

const env = Object.fromEntries(
  (await readFile(new URL('../.env', import.meta.url), 'utf8'))
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => [
      line.slice(0, line.indexOf('=')),
      line.slice(line.indexOf('=') + 1),
    ]),
);
const issuer = 'http://auth.excellore.localhost:8080/realms/excellore';
const api = 'http://127.0.0.1:5002';
const tokenResponse = await fetch(`${issuer}/protocol/openid-connect/token`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  body: new URLSearchParams({
    grant_type: 'client_credentials',
    client_id: 'svc-catalog-sync',
    client_secret: env.SVC_CATALOG_SYNC_SECRET,
  }),
});
assert.equal(tokenResponse.status, 200, 'local catalog machine token');
const { access_token: token } = await tokenResponse.json();
for (const app of ['access']) {
  const document = parse(
    await readFile(
      new URL(`../services/${app}/permissions.yaml`, import.meta.url),
      'utf8',
    ),
  );
  const catalog = {
    app,
    permissions: document.permissions.map((permission) => ({
      ...permission,
      privileged: permission.privileged ?? false,
      ownerOnly: permission.ownerOnly ?? false,
      deprecated: permission.deprecated ?? false,
      systemRoles: permission.systemRoles ?? [],
    })),
  };
  const response = await fetch(`${api}/internal/v1/catalog/${app}`, {
    method: 'PUT',
    headers: {
      Authorization: `Bearer ${token}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(catalog),
  });
  assert.equal(response.status, 204, `${app} local catalog sync`);
}
process.stdout.write('Local Access permission catalog synchronized.\n');
