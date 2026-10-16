import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Reconcile existing local realms without resetting development users.
const env = Object.fromEntries(
  readFileSync(new URL('../.env', import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => [
      line.slice(0, line.indexOf('=')),
      line.slice(line.indexOf('=') + 1),
    ]),
);
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
const root = `${base}/admin/realms/excellore`;
async function get(path) {
  const response = await fetch(`${root}${path}`, {
    headers: { authorization: `Bearer ${token}` },
  });
  assert.equal(response.status, 200, `local worker directory GET ${path}`);
  return response.json();
}
async function grant(path, role) {
  const response = await fetch(`${root}${path}`, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify([role]),
  });
  assert.equal(response.status, 204, 'local worker directory role grant');
}
const clients = await get('/clients');
const management = clients.find(
  (client) => client.clientId === 'realm-management',
);
assert.ok(management?.id, 'realm-management client');
const role = await get(`/clients/${management.id}/roles/view-users`);
for (const clientId of ['worker-access', 'worker-profile']) {
  const client = clients.find((item) => item.clientId === clientId);
  assert.ok(client?.id, `${clientId} client`);
  const scopePath = `/clients/${client.id}/scope-mappings/clients/${management.id}`;
  if (!(await get(scopePath)).some((item) => item.name === role.name))
    await grant(scopePath, role);

  const service = await get(`/clients/${client.id}/service-account-user`);
  assert.equal(service.username, `service-account-${clientId}`);
  assert.match(service.id ?? '', /^[0-9a-f-]{36}$/i);
  // This UUID identifies a machine account, never a person.
  const userPath = `/users/${service.id}/role-mappings/clients/${management.id}`;
  if (!(await get(userPath)).some((item) => item.name === role.name))
    await grant(userPath, role);
}
console.log('Local worker directory roles reconciled.');
