import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const env = Object.fromEntries(
  readFileSync(new URL('../.env', import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => {
      const separator = line.indexOf('=');
      return [line.slice(0, separator), line.slice(separator + 1)];
    }),
);

const origin = new URL(env.ACCESS_ORIGIN);
assert.equal(origin.hostname.endsWith('.localhost'), true);
assert.equal(origin.pathname, '/');
assert.equal(origin.search, '');
assert.equal(origin.hash, '');
const base = 'http://auth.excellore.localhost:8080';
const login = await fetch(
  `${base}/realms/master/protocol/openid-connect/token`,
  {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'password',
      client_id: 'admin-cli',
      username: 'admin',
      password: env.KEYCLOAK_ADMIN_PASSWORD,
    }),
  },
);
assert.equal(login.status, 200, 'local Keycloak admin login');
const { access_token: token } = await login.json();
const endpoint = `${base}/admin/realms/excellore/clients`;
const clientsResponse = await fetch(endpoint, {
  headers: { authorization: `Bearer ${token}` },
});
assert.equal(clientsResponse.status, 200, 'local Keycloak client list');
const client = (await clientsResponse.json()).find(
  (candidate) => candidate.clientId === 'excellore-access',
);
assert.ok(client?.id, 'local Access BFF client');
const expected = [
  `${env.ACCESS_ORIGIN}/auth/callback`,
  `${env.ACCESS_ORIGIN}/invitations/accept`,
];
if (JSON.stringify(client.redirectUris) !== JSON.stringify(expected)) {
  const update = await fetch(`${endpoint}/${client.id}`, {
    method: 'PUT',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({ ...client, redirectUris: expected }),
  });
  assert.equal(update.status, 204, 'local Access redirect update');
}
console.log('Local Access invitation redirect matches the generated realm.');
