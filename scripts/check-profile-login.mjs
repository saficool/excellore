import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
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
const appOrigin = env.PROFILE_ORIGIN;
assert.ok(
  appOrigin && env.DEV_USER_PASSWORD && env.DEV_OTP_SECRET,
  'Local stack is not initialized',
);

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
let response = await browser.request(
  `${appOrigin}/auth/login?returnTo=%2Faccount`,
);
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
assert.equal(response.headers.get('location'), `${appOrigin}/account`);
response = await browser.request(`${appOrigin}/account`);
assert.equal(response.status, 200, 'Signed-in account page unavailable');
const accountHtml = await response.text();
assert.match(accountHtml, /Signed in/);
assert.equal(
  /(?:access_token|refresh_token|id_token)/i.test(accountHtml),
  false,
  'Tokens reached the browser page',
);
assert.match(
  response.headers.get('content-security-policy') ?? '',
  /frame-ancestors 'none'/,
);
response = await browser.request(`${appOrigin}/auth/session`);
assert.deepEqual(Object.keys(await response.json()).sort(), [
  'acr',
  'authenticated',
]);
response = await browser.request(`${appOrigin}/auth/logout`, {
  method: 'POST',
  headers: { Origin: appOrigin },
});
assert.equal(response.status, 303, 'RP logout failed');
response = await browser.request(`${appOrigin}/auth/session`);
assert.deepEqual(await response.json(), { authenticated: false });
process.stdout.write(
  'Generated Profile app login, session, CSP, and logout passed.\n',
);
