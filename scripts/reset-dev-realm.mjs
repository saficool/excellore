import { readFileSync } from 'node:fs';

if (process.env.EXCELLORE_DEV_REALM_RESET !== '1') {
  throw new Error(
    'Set EXCELLORE_DEV_REALM_RESET=1 to reset the local development realm.',
  );
}

const env = Object.fromEntries(
  readFileSync(new URL('../.env', import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => {
      const separator = line.indexOf('=');
      return [line.slice(0, separator), line.slice(separator + 1)];
    }),
);

const baseUrl = 'http://auth.excellore.localhost:8080';
const tokenResponse = await fetch(
  `${baseUrl}/realms/master/protocol/openid-connect/token`,
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
if (!tokenResponse.ok) {
  throw new Error(
    `Local Keycloak admin login failed (${tokenResponse.status}).`,
  );
}
const { access_token: token } = await tokenResponse.json();
const deleteResponse = await fetch(`${baseUrl}/admin/realms/excellore`, {
  method: 'DELETE',
  headers: { authorization: `Bearer ${token}` },
});
if (!deleteResponse.ok && deleteResponse.status !== 404) {
  throw new Error(
    `Local development realm reset failed (${deleteResponse.status}).`,
  );
}
console.log(
  'Local excellore realm removed. Restart the Keycloak container to re-import it.',
);
