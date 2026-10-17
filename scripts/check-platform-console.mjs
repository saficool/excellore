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
const origin = env.PLATFORM_ORIGIN;
assert.ok(
  new URL(origin).hostname.endsWith('.localhost') &&
    env.DEV_USER_PASSWORD &&
    env.DEV_OTP_SECRET,
  'Local Access stack is not initialized',
);

class Browser {
  cookies = new Map();
  async request(url, options = {}) {
    const host = new URL(url).origin;
    const jar = this.cookies.get(host) ?? new Map();
    const headers = new Headers(options.headers);
    if (jar.size)
      headers.set(
        'Cookie',
        [...jar].map(([name, value]) => `${name}=${value}`).join('; '),
      );
    const response = await fetch(url, {
      ...options,
      headers,
      redirect: 'manual',
    });
    for (const item of response.headers.getSetCookie()) {
      const pair = item.split(';')[0];
      const separator = pair.indexOf('=');
      if (pair.slice(separator + 1))
        jar.set(pair.slice(0, separator), pair.slice(separator + 1));
      else jar.delete(pair.slice(0, separator));
    }
    this.cookies.set(host, jar);
    return response;
  }
}

function formAction(html) {
  const match = html.match(/<form\b[^>]*\baction="([^"]+)"/i);
  assert.ok(match, 'Keycloak login form');
  return match[1].replaceAll('&amp;', '&');
}

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

const browser = new Browser();
let response = await browser.request(`${origin}/auth/login?returnTo=%2F`);
assert.equal(response.status, 302, 'Platform BFF login');
response = await browser.request(response.headers.get('location'));
assert.equal(response.status, 200, 'Keycloak login');
let html = await response.text();
response = await browser.request(formAction(html), {
  method: 'POST',
  headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  body: new URLSearchParams({
    username: 'penny.platform',
    password: env.DEV_USER_PASSWORD,
  }),
});
if (response.status === 200) {
  html = await response.text();
  assert.match(html, /name="otp"/i);
  await new Promise((resolve) =>
    setTimeout(resolve, 31_000 - (Date.now() % 30_000)),
  );
  response = await browser.request(formAction(html), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ otp: otp() }),
  });
}
assert.equal(response.status, 302, 'Keycloak callback');
response = await browser.request(response.headers.get('location'));
assert.equal(response.status, 302, 'Platform BFF session');
assert.equal(new URL(response.headers.get('location')).pathname, '/');

response = await browser.request(`${origin}/`);
assert.equal(response.status, 200, 'platform page');
html = await response.text();
assert.match(html, /Platform console/);
assert.match(html, /Platform staff/);
assert.doesNotMatch(html, /server-only-token|access_token|refresh_token/i);

response = await browser.request(`${origin}/api/platform/me`);
assert.equal(response.status, 200, 'platform role endpoint');
assert.ok((await response.json()).roles.includes('platform-operator'));
response = await browser.request(`${origin}/api/platform/tenants`);
assert.equal(response.status, 200, 'platform tenant metadata');
assert.ok(Array.isArray(await response.json()));
response = await browser.request(`${origin}/api/platform/audit-events`);
assert.equal(response.status, 403, 'operator cannot read auditor endpoint');
response = await browser.request(`${origin}/api/platform/tenants`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: '{}',
});
assert.equal(response.status, 403, 'platform proxy enforces CSRF');

console.log('Platform login, role gate, metadata read, and CSRF verified.');
