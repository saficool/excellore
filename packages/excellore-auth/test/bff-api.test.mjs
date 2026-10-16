import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import {
  canonicalBffRequest,
  proxyBffApi,
  validateApiProxyRequest,
} from '../dist/index.js';

const sid = randomBytes(32).toString('base64url');
const appOrigin = 'http://profile.excellore.localhost:3001';
const config = {
  APP_BASE_URL: appOrigin,
  SESSION_COOKIE_NAME: 'excellore-dev-profile',
  EXCELLORE_INSECURE_LOCAL_DEV: 'true',
};
const session = {
  sid,
  sub: '3f20d34c-23ac-41a2-9d4b-82a6a12f0ce1',
  accessToken: 'server-only-token',
  accessTokenExpiresAt: Math.floor(Date.now() / 1000) + 600,
};
const store = {
  async get(value) {
    return value === sid ? session : null;
  },
};
const cookie = { Cookie: `excellore-dev-profile=${sid}` };
const tenantId = '7cb18bd1-6a74-4962-a72a-7ad5f87b0621';

test('tenant proxy denies non-members before any app API call', async () => {
  let called = false;
  const response = await proxyBffApi({
    config,
    store,
    oidcClient: {},
    app: 'profile',
    apiBaseUrl: 'http://localhost:5001',
    scope: 'tenant',
    slug: 'other-tenant',
    segments: ['records'],
    request: new Request(`${appOrigin}/api/t/other-tenant/records`, {
      headers: cookie,
    }),
    access: {
      async resolveSlug() {
        return null;
      },
      async resolve() {
        throw new Error('unreachable');
      },
    },
    fetchImpl: async () => {
      called = true;
      return Response.json({});
    },
  });
  assert.equal(response.status, 404);
  assert.equal(called, false);
});

test('tenant proxy uses only Access resolved tenant ID, never browser path', async () => {
  let destination;
  const response = await proxyBffApi({
    config,
    store,
    oidcClient: {},
    app: 'profile',
    apiBaseUrl: 'http://localhost:5001',
    scope: 'tenant',
    slug: 'my-tenant',
    segments: ['records'],
    request: new Request(`${appOrigin}/api/t/my-tenant/records`, {
      headers: cookie,
    }),
    access: {
      async resolveSlug() {
        return { tenantId, slug: 'my-tenant', name: 'Tenant' };
      },
      async resolve() {
        return {
          tenantId,
          decision: 'allow',
          apps: ['profile'],
          permissions: ['profile.app.access'],
        };
      },
    },
    fetchImpl: async (url, options) => {
      destination = String(url);
      assert.equal(options.headers.Authorization, 'Bearer server-only-token');
      return Response.json({ ok: true });
    },
  });
  assert.equal(response.status, 200);
  assert.equal(
    destination,
    `http://localhost:5001/v1/tenants/${tenantId}/records`,
  );
  assert.equal(response.headers.get('cache-control'), 'no-store');
});

test('proxy accepts an empty accepted response and rejects non-JSON content', async () => {
  const options = {
    config,
    store,
    oidcClient: {},
    app: 'profile',
    apiBaseUrl: 'http://localhost:5001',
    scope: 'me',
    segments: ['invitations'],
    request: new Request(`${appOrigin}/api/me/invitations`, {
      method: 'POST',
      headers: {
        ...cookie,
        Origin: appOrigin,
        'X-Excellore-CSRF': '1',
      },
    }),
  };
  const accepted = await proxyBffApi({
    ...options,
    fetchImpl: async () => new Response('', { status: 202 }),
  });
  assert.equal(accepted.status, 202);
  assert.equal(await accepted.text(), '');
  const rejected = await proxyBffApi({
    ...options,
    fetchImpl: async () => new Response('private', { status: 200 }),
  });
  assert.equal(rejected.status, 503);
  assert.doesNotMatch(await rejected.text(), /private/);
});

test('mutating API requests require origin and CSRF header', () => {
  const bad = new Request(`${appOrigin}/api/me/settings`, {
    method: 'POST',
    headers: { Origin: appOrigin },
  });
  assert.equal(validateApiProxyRequest(bad, appOrigin, ['settings']), 403);
  const good = new Request(`${appOrigin}/api/me/settings`, {
    method: 'POST',
    headers: { Origin: appOrigin, 'X-Excellore-CSRF': '1' },
  });
  assert.equal(validateApiProxyRequest(good, appOrigin, ['settings']), null);
  assert.equal(
    validateApiProxyRequest(
      new Request(`${appOrigin}/api/me/settings?email=private`),
      appOrigin,
      ['settings'],
    ),
    400,
  );
});

test('tenant proxy accepts only known colon action segments', () => {
  const request = new Request(`${appOrigin}/api/t/my-tenant/members:suspend`, {
    method: 'POST',
    headers: { Origin: appOrigin, 'X-Excellore-CSRF': '1' },
  });
  assert.equal(
    validateApiProxyRequest(request, appOrigin, ['members:suspend']),
    null,
  );
  assert.equal(
    validateApiProxyRequest(request, appOrigin, ['unknown:action']),
    400,
  );
});

test('Next.js internal bind URL is canonicalized only for the configured Host', async () => {
  const source = new Request('http://0.0.0.0:3001/auth/logout', {
    method: 'POST',
    headers: { Host: 'profile.excellore.localhost:3001', Origin: appOrigin },
    body: 'logout',
  });
  const canonical = canonicalBffRequest(source, appOrigin);
  assert.equal(canonical?.url, `${appOrigin}/auth/logout`);
  assert.equal(await canonical.text(), 'logout');
  assert.equal(
    canonicalBffRequest(
      new Request('http://0.0.0.0:3001/auth/login', {
        headers: { Host: 'evil.localhost:3001' },
      }),
      appOrigin,
    ),
    null,
  );
});
