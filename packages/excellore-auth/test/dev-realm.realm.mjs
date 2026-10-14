import assert from 'node:assert/strict';
import { createHmac, randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import Valkey from 'iovalkey';
import * as oidc from 'openid-client';
import {
  createAuthHandlers,
  createOidcClient,
  getAccessToken,
  loadAuthConfig,
  ValkeySessionStore,
} from '../dist/index.js';

const env = Object.fromEntries(
  readFileSync(new URL('../../../.env', import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => [
      line.slice(0, line.indexOf('=')),
      line.slice(line.indexOf('=') + 1),
    ]),
);
const issuer = process.env.EXCELLORE_DEV_ISSUER;
const username = process.env.EXCELLORE_DEV_USERNAME;
const valkeyUrl = process.env.EXCELLORE_DEV_VALKEY_URL;
assert.ok(
  issuer && username && valkeyUrl,
  'Set EXCELLORE_DEV_ISSUER, EXCELLORE_DEV_USERNAME, and EXCELLORE_DEV_VALKEY_URL',
);

class Browser {
  cookies = new Map();
  async request(url, options = {}) {
    const headers = new Headers(options.headers);
    if (this.cookies.size)
      headers.set(
        'cookie',
        [...this.cookies].map(([key, value]) => `${key}=${value}`).join('; '),
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
        this.cookies.set(pair.slice(0, cut), pair.slice(cut + 1));
      else this.cookies.delete(pair.slice(0, cut));
    }
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
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const digest = createHmac('sha1', Buffer.from(secret, 'utf8'))
    .update(counter)
    .digest();
  const offset = digest.at(-1) & 15;
  return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1000000)
    .toString()
    .padStart(6, '0');
}

test('dev realm: shared handlers complete login, callback, locked refresh and RP logout', async () => {
  const redis = new Valkey(valkeyUrl);
  const prefix = `wp04-${randomBytes(6).toString('hex')}:sess:`;
  try {
    const config = loadAuthConfig({
      OIDC_ISSUER: issuer,
      OIDC_CLIENT_ID: 'excellore-profile',
      OIDC_CLIENT_SECRET: env.PROFILE_BFF_SECRET,
      OIDC_SCOPES: 'openid profile email api-profile',
      APP_BASE_URL: env.PROFILE_ORIGIN,
      SESSION_COOKIE_NAME: 'excellore-dev-profile',
      SESSION_STORE_URL: valkeyUrl,
      SESSION_KEY_PREFIX: prefix,
      SESSION_ENCRYPTION_KEY: randomBytes(32).toString('base64url'),
      EXCELLORE_INSECURE_LOCAL_DEV: 'true',
      NODE_ENV: 'test',
    });
    const client = await createOidcClient(config);
    const store = new ValkeySessionStore(
      redis,
      prefix,
      config.SESSION_ENCRYPTION_KEY,
    );
    const handlers = createAuthHandlers(config, store, client);
    const started = await handlers.login(
      new Request(`${config.APP_BASE_URL}/auth/login?returnTo=%2F`),
    );
    assert.equal(started.status, 302);
    const flowCookie = started.headers.getSetCookie()[0].split(';')[0];
    const browser = new Browser();
    let response = await browser.request(started.headers.get('location'));
    assert.equal(response.status, 200);
    let html = await response.text();
    response = await browser.request(formAction(html), {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ username, password: env.DEV_USER_PASSWORD }),
    });
    if (response.status === 200) {
      html = await response.text();
      assert.match(html, /name="otp"/i);
      const delay = 31_000 - (Date.now() % 30_000);
      await new Promise((resolve) => setTimeout(resolve, delay));
      response = await browser.request(formAction(html), {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({ otp: otp(env.DEV_OTP_SECRET) }),
      });
    }
    assert.equal(response.status, 302, 'Keycloak login did not complete');
    const callbackUrl = response.headers.get('location');
    const callback = await handlers.callback(
      new Request(callbackUrl, { headers: { Cookie: flowCookie } }),
    );
    assert.equal(
      callback.status,
      302,
      'Shared callback rejected the dev realm',
    );
    const cookie = callback.headers
      .getSetCookie()
      .find((item) => item.startsWith('excellore-dev-profile='));
    const sid = cookie.split(';')[0].split('=')[1];
    const initial = await store.get(sid);
    assert.ok(initial && initial.kcSid && initial.refreshToken);
    await store.update(sid, (record) => ({
      ...record,
      accessTokenExpiresAt: Math.floor(Date.now() / 1000) + 10,
    }));
    const [a, b] = await Promise.all([
      getAccessToken(sid, store, client),
      getAccessToken(sid, store, client),
    ]);
    assert.equal(a, b);
    assert.notEqual(a, initial.accessToken);
    const refreshed = await store.get(sid);
    const logout = await handlers.logout(
      new Request(`${config.APP_BASE_URL}/auth/logout`, {
        method: 'POST',
        headers: {
          Cookie: `excellore-dev-profile=${sid}`,
          Origin: config.APP_BASE_URL,
        },
      }),
    );
    assert.equal(logout.status, 303, 'Keycloak rejected server-side RP logout');
    assert.equal(await store.get(sid), null);
    await assert.rejects(
      oidc.refreshTokenGrant(client, refreshed.refreshToken),
      'Keycloak retained the SSO session after logout',
    );
  } finally {
    const keys = await redis.keys(`${prefix}*`);
    if (keys.length) await redis.del(...keys);
    redis.disconnect();
  }
});
