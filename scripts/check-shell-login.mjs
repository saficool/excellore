import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { createServer } from 'node:http';
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
const appOrigin = env.SHELL_ORIGIN;
assert.ok(
  appOrigin && env.DEV_USER_PASSWORD && env.DEV_OTP_SECRET,
  'Local stack is not initialized',
);

const alphaId = '54784917-9764-4969-ad2a-86c039b41062';
const betaId = '3bf14634-ff14-4820-92b3-2ed3d7862249';
let oneTenant = false;
const mockAccess = createServer(async (request, response) => {
  let body = '';
  for await (const chunk of request) body += chunk;
  const input = JSON.parse(body);
  assert.match(input.userId, /^[0-9a-f-]{36}$/);
  assert.equal(request.method, 'POST');
  assert.ok(request.headers.authorization?.startsWith('Bearer '));
  const rows = [
    {
      tenantId: alphaId,
      slug: 'alpha',
      name: 'Alpha',
      status: 'active',
      roles: ['member'],
      apps: ['profile'],
    },
    {
      tenantId: betaId,
      slug: 'beta',
      name: 'Beta',
      status: 'active',
      roles: ['member'],
      apps: ['access'],
    },
  ];
  let value;
  if (request.url === '/internal/v1/users/tenants:list')
    value = oneTenant ? rows.slice(0, 1) : rows;
  else if (request.url === '/internal/v1/users/invitations:list')
    value = [
      {
        id: 'ed5024e2-b258-465c-90b4-a664e629ac59',
        tenantId: betaId,
        tenantName: 'Beta',
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
      },
    ];
  else if (request.url === '/internal/v1/tenants:resolve-slug') {
    const row = rows.find((tenant) => tenant.slug === input.slug);
    if (row) value = { tenantId: row.tenantId, slug: row.slug, name: row.name };
  } else if (request.url === '/internal/v1/authz/resolve') {
    const row = rows.find((tenant) => tenant.tenantId === input.tenantId);
    if (row)
      value = {
        tenantId: row.tenantId,
        decision: 'allow',
        tenantStatus: 'active',
        membershipStatus: 'active',
        roles: ['member'],
        permissions: row.apps.map((app) => `${app}.app.access`),
        apps: row.apps,
        version: 1,
        resolvedAt: new Date().toISOString(),
      };
  }
  response.writeHead(value ? 200 : 404, { 'Content-Type': 'application/json' });
  response.end(value ? JSON.stringify(value) : '{}');
});
await new Promise((resolve) => mockAccess.listen(5002, '127.0.0.1', resolve));

class Browser {
  cookies = new Map();
  async request(url, options = {}) {
    const origin = new URL(url).origin;
    const jar = this.cookies.get(origin) ?? new Map();
    const headers = new Headers(options.headers);
    if (jar.size)
      headers.set(
        'Cookie',
        [...jar].map(([key, value]) => `${key}=${value}`).join('; '),
      );
    const response = await fetch(url, {
      ...options,
      headers,
      redirect: 'manual',
    });
    for (const item of response.headers.getSetCookie()) {
      const pair = item.split(';')[0];
      const cut = pair.indexOf('=');
      if (pair.slice(cut + 1)) jar.set(pair.slice(0, cut), pair.slice(cut + 1));
      else jar.delete(pair.slice(0, cut));
    }
    this.cookies.set(origin, jar);
    return response;
  }
}

function formAction(html) {
  const match = html.match(/<form\b[^>]*\baction="([^"]+)"/i);
  assert.ok(match, 'Keycloak login form missing');
  return match[1].replaceAll('&amp;', '&');
}

function otp(secret) {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30_000)));
  const digest = createHmac('sha1', Buffer.from(secret, 'utf8'))
    .update(counter)
    .digest();
  const offset = digest.at(-1) & 15;
  return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000)
    .toString()
    .padStart(6, '0');
}

const browser = new Browser();
let response = await browser.request(`${appOrigin}/auth/login?returnTo=%2F`);
assert.equal(response.status, 302, 'Generated app did not start OIDC login');
const loginUrl = response.headers.get('location');
assert.ok(
  loginUrl?.startsWith(
    'http://auth.excellore.localhost:8080/realms/excellore/',
  ),
  'Unexpected login destination',
);
response = await browser.request(loginUrl);
assert.equal(response.status, 200, 'Keycloak login form unavailable');
let html = await response.text();
response = await browser.request(formAction(html), {
  method: 'POST',
  headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  body: new URLSearchParams({
    username: 'olivia.owner',
    password: env.DEV_USER_PASSWORD,
  }),
});
if (response.status === 200) {
  html = await response.text();
  assert.match(html, /name="otp"/i, 'Expected OTP challenge');
  const wait = 31_000 - (Date.now() % 30_000);
  await new Promise((resolve) => setTimeout(resolve, wait));
  response = await browser.request(formAction(html), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ otp: otp(env.DEV_OTP_SECRET) }),
  });
}
assert.equal(response.status, 302, 'Keycloak did not complete login');
assert.ok(
  response.headers.get('location')?.startsWith(`${appOrigin}/auth/callback?`),
  'Unexpected callback',
);
response = await browser.request(response.headers.get('location'));
assert.equal(response.status, 302, 'Generated app rejected callback');
assert.equal(response.headers.get('location'), `${appOrigin}/`);
response = await browser.request(`${appOrigin}/auth/session`);
assert.equal(response.status, 200, 'Signed-in session unavailable');
assert.match(
  response.headers.get('content-security-policy') ?? '',
  /frame-ancestors 'none'/,
);
assert.deepEqual(Object.keys(await response.json()).sort(), [
  'acr',
  'authenticated',
]);
response = await browser.request(`${appOrigin}/`);
assert.equal(response.status, 200, 'Tenant picker unavailable');
let page = await response.text();
assert.match(page, /Alpha/);
assert.match(page, /Beta/);
assert.match(page, /Pending invitations/);
assert.doesNotMatch(page, /(?:access_token|refresh_token|id_token)/i);
response = await browser.request(`${appOrigin}/t/alpha`);
assert.equal(response.status, 200);
page = await response.text();
assert.match(page, />Profile<|>Profile<!-- -->/);
assert.match(page, /Your account and your profile in this organization/);
assert.match(page, />Workspaces</);
assert.doesNotMatch(page, /Open Access/);
response = await browser.request(`${appOrigin}/t/beta`);
assert.equal(response.status, 200);
page = await response.text();
assert.match(page, />Access<|>Access<!-- -->/);
assert.match(page, /People, roles, and permissions/);
response = await browser.request(`${appOrigin}/t/unknown`);
assert.equal(response.status, 404);
response = await browser.request(`${appOrigin}/t/gamma`);
assert.equal(response.status, 404);
oneTenant = true;
response = await browser.request(`${appOrigin}/`);
assert.equal(response.status, 307);
assert.equal(response.headers.get('location'), '/t/alpha');
const profileOrigin = env.PROFILE_ORIGIN;
response = await browser.request(
  `${profileOrigin}/auth/login?returnTo=%2Faccount`,
);
assert.equal(response.status, 302);
response = await browser.request(response.headers.get('location'));
if (response.status === 200) {
  html = await response.text();
  response = await browser.request(formAction(html), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      username: 'olivia.owner',
      password: env.DEV_USER_PASSWORD,
    }),
  });
  if (response.status === 200) {
    html = await response.text();
    assert.match(html, /name="otp"/i);
    const wait = 31_000 - (Date.now() % 30_000);
    await new Promise((resolve) => setTimeout(resolve, wait));
    response = await browser.request(formAction(html), {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ otp: otp(env.DEV_OTP_SECRET) }),
    });
  }
}
assert.equal(response.status, 302, 'Keycloak SSO did not authorize Profile');
response = await browser.request(response.headers.get('location'));
assert.equal(response.status, 302, 'Profile did not accept the SSO callback');
response = await browser.request(`${profileOrigin}/auth/session`);
assert.equal((await response.json()).authenticated, true);
response = await browser.request(`${appOrigin}/auth/logout`, {
  method: 'POST',
  headers: { Origin: appOrigin },
});
assert.equal(response.status, 303, 'RP logout failed');
response = await browser.request(`${appOrigin}/auth/session`);
assert.deepEqual(await response.json(), { authenticated: false });
let profileSignedOut = false;
for (let attempt = 0; attempt < 20; attempt++) {
  response = await browser.request(`${profileOrigin}/auth/session`);
  if ((await response.json()).authenticated === false) {
    profileSignedOut = true;
    break;
  }
  await new Promise((resolve) => setTimeout(resolve, 250));
}
assert.ok(profileSignedOut, 'Shell logout did not close Profile BFF session');
await new Promise((resolve) => mockAccess.close(resolve));
process.stdout.write(
  'Shell login, picker, gated launcher, 404, CSP, and cross-app logout passed.\n',
);
