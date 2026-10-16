import assert from 'node:assert/strict';
import { createHash, createHmac, randomBytes } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const env = Object.fromEntries(
  (await readFile(new URL('../.env', import.meta.url), 'utf8'))
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => [
      line.slice(0, line.indexOf('=')),
      line.slice(line.indexOf('=') + 1),
    ]),
);
assert.equal(
  new URL(env.ACCESS_ORIGIN).hostname.endsWith('.localhost'),
  true,
  'Development seed requires a local Access origin',
);
const realm = JSON.parse(
  await readFile(
    new URL('../infra/keycloak/realm-dev.json', import.meta.url),
    'utf8',
  ),
);
const ownerEmail = realm.users.find(
  (user) => user.username === 'olivia.owner',
)?.email;
assert.ok(
  ownerEmail &&
    env.DEV_USER_PASSWORD &&
    env.DEV_OTP_SECRET &&
    env.ACCESS_BFF_SECRET,
);
const issuer = 'http://auth.excellore.localhost:8080/realms/excellore';
const callback = `${env.ACCESS_ORIGIN}/auth/callback`;
const api = 'http://127.0.0.1:5002';

function otp() {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30_000)));
  const digest = createHmac('sha1', Buffer.from(env.DEV_OTP_SECRET, 'utf8'))
    .update(counter)
    .digest();
  const offset = digest.at(-1) & 15;
  return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000)
    .toString()
    .padStart(6, '0');
}

function formAction(html) {
  const match = html.match(/<form\b[^>]*\baction="([^"]+)"/i);
  assert.ok(match, 'Keycloak login form');
  return match[1].replaceAll('&amp;', '&');
}

async function login(username) {
  const cookies = new Map();
  async function request(url, options = {}) {
    const headers = new Headers(options.headers);
    if (cookies.size)
      headers.set(
        'Cookie',
        [...cookies].map(([key, value]) => `${key}=${value}`).join('; '),
      );
    const response = await fetch(url, {
      ...options,
      headers,
      redirect: 'manual',
    });
    for (const item of response.headers.getSetCookie()) {
      const pair = item.split(';')[0];
      const cut = pair.indexOf('=');
      if (pair.slice(cut + 1))
        cookies.set(pair.slice(0, cut), pair.slice(cut + 1));
      else cookies.delete(pair.slice(0, cut));
    }
    return response;
  }
  const verifier = randomBytes(32).toString('base64url');
  const challenge = createHash('sha256').update(verifier).digest('base64url');
  const authorization = new URL(`${issuer}/protocol/openid-connect/auth`);
  authorization.search = new URLSearchParams({
    client_id: 'excellore-access',
    redirect_uri: callback,
    response_type: 'code',
    scope: 'openid profile email api-access',
    state: randomBytes(8).toString('hex'),
    code_challenge: challenge,
    code_challenge_method: 'S256',
    acr_values: '2',
  });
  let response = await request(authorization);
  assert.equal(response.status, 200, 'Keycloak login page');
  let html = await response.text();
  response = await request(formAction(html), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ username, password: env.DEV_USER_PASSWORD }),
  });
  if (response.status === 200) {
    html = await response.text();
    assert.match(html, /name="otp"/i, 'Keycloak OTP prompt');
    await new Promise((resolve) =>
      setTimeout(resolve, 31_000 - (Date.now() % 30_000)),
    );
    response = await request(formAction(html), {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ otp: otp() }),
    });
  }
  assert.equal(response.status, 302, 'Keycloak authorization');
  const returned = new URL(response.headers.get('location'));
  assert.equal(returned.origin + returned.pathname, callback);
  const tokenResponse = await fetch(`${issuer}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      client_id: 'excellore-access',
      client_secret: env.ACCESS_BFF_SECRET,
      grant_type: 'authorization_code',
      redirect_uri: callback,
      code: returned.searchParams.get('code'),
      code_verifier: verifier,
    }),
  });
  assert.equal(tokenResponse.status, 200, 'Keycloak token exchange');
  return (await tokenResponse.json()).access_token;
}

async function accessRequest(token, path, method = 'GET', body) {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  return response;
}

const ownerToken = await login('olivia.owner');
let response = await accessRequest(ownerToken, '/v1/me/tenants');
assert.equal(response.status, 200, 'owner tenant list');
const active = await response.json();
if (active.some((tenant) => tenant.slug === 'acme')) {
  process.stdout.write('Local Acme owner is already active.\n');
  process.exit(0);
}

const operatorToken = await login('penny.platform');
response = await accessRequest(operatorToken, '/v1/platform/tenants', 'POST', {
  name: 'Acme',
  slug: 'acme',
  plan: 'starter',
  ownerEmail,
  initialApps: [],
});
let invitationId;
let tenantId;
if (response.status === 201) {
  const created = await response.json();
  invitationId = created.ownerInvitationId;
  tenantId = created.tenantId;
} else {
  assert.equal(response.status, 409, 'Acme tenant provisioning');
  response = await accessRequest(ownerToken, '/v1/me/invitations');
  assert.equal(response.status, 200, 'pending owner invitations');
  const pending = (await response.json()).find(
    (item) => item.tenantName === 'Acme',
  );
  assert.ok(pending, 'pending Acme invitation');
  invitationId = pending.id;
  tenantId = pending.tenantId;
}

const mailbox = await (
  await fetch('http://127.0.0.1:8025/api/v1/messages')
).json();
let oneTimeCode;
for (const message of mailbox.messages) {
  if (message.Subject !== 'Your Excellore invitation') continue;
  const detail = await (
    await fetch(
      `http://127.0.0.1:8025/api/v1/message/${encodeURIComponent(message.ID)}`,
    )
  ).json();
  if (!detail.Text?.includes(invitationId)) continue;
  oneTimeCode = detail.Text.match(
    /Enter this one-time invitation code: (\S+)/,
  )?.[1];
  break;
}
assert.ok(oneTimeCode, 'matching Mailpit invitation code');
response = await accessRequest(
  ownerToken,
  '/v1/me/invitations/accept',
  'POST',
  {
    invitationId,
    oneTimeCode,
  },
);
assert.equal(response.status, 200, 'owner invitation acceptance');
assert.equal((await response.json()).tenantId, tenantId);
response = await accessRequest(ownerToken, `/v1/tenants/${tenantId}/me/access`);
assert.equal(response.status, 200, 'owner tenant access');
const snapshot = await response.json();
assert.ok(snapshot.roles.includes('owner'));
assert.ok(snapshot.permissions.includes('access.app.access'));
process.stdout.write('Local Acme owner is active.\n');
