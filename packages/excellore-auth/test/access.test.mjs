import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { after, before, test } from 'node:test';
import {
  AccessUnavailableError,
  createAccessClient,
  loadAccessClientConfig,
  loadAuthConfig,
  requireSession,
  requireTenant,
  SessionRequiredError,
  TenantAccessError,
} from '../dist/index.js';

const userId = '3a7c0e29-6c2d-4908-b9a5-496e869862d2';
const tenantId = '89aaf511-1221-441d-8aee-3f4a295ee70b';
const otherTenantId = 'ec73dbb7-5178-4430-aaee-fb7fbe6168b0';
let server;
let origin;
let config;
let access;
let client;
let tokenCalls = 0;
let calls = [];
let mode = 'allow';

function json(response, data, status = 200) {
  response.writeHead(status, { 'Content-Type': 'application/json' });
  response.end(JSON.stringify(data));
}

before(async () => {
  server = createServer(async (request, response) => {
    if (request.url === '/.well-known/openid-configuration') {
      json(response, {
        issuer: origin,
        authorization_endpoint: `${origin}/authorize`,
        token_endpoint: `${origin}/token`,
        jwks_uri: `${origin}/jwks`,
        response_types_supported: ['code'],
        grant_types_supported: ['client_credentials'],
        subject_types_supported: ['public'],
        id_token_signing_alg_values_supported: ['RS256'],
        token_endpoint_auth_methods_supported: ['client_secret_post'],
      });
      return;
    }
    let body = '';
    for await (const chunk of request) body += chunk.toString();
    if (request.url === '/token') {
      const form = new URLSearchParams(body);
      if (
        form.get('client_id') !== 'svc-profile-authz' ||
        form.get('client_secret') !== 'machine-secret' ||
        form.get('grant_type') !== 'client_credentials'
      ) {
        response.writeHead(401).end();
        return;
      }
      tokenCalls += 1;
      json(response, {
        access_token: 'machine-token',
        token_type: 'Bearer',
        expires_in: 300,
      });
      return;
    }
    calls.push({ method: request.method, url: request.url, body });
    if (
      request.headers.authorization !== 'Bearer machine-token' ||
      request.method !== 'POST'
    ) {
      response.writeHead(401).end();
      return;
    }
    if (mode === 'unavailable') {
      response.writeHead(503).end();
      return;
    }
    const input = JSON.parse(body);
    if (input.userId !== userId) {
      response.writeHead(404).end();
      return;
    }
    if (request.url === '/internal/v1/tenants:resolve-slug') {
      if (mode === 'nonmember') {
        response.writeHead(404).end();
        return;
      }
      json(response, {
        tenantId,
        slug: mode === 'wrong-slug' ? 'other' : input.slug,
        name: 'Example tenant',
      });
      return;
    }
    if (request.url === '/internal/v1/users/tenants:list') {
      json(response, [
        {
          tenantId,
          slug: 'example',
          name: 'Example tenant',
          status: 'active',
          roles: ['member'],
          apps: ['profile'],
        },
      ]);
      return;
    }
    if (request.url === '/internal/v1/users/invitations:list') {
      json(response, [
        {
          id: '16980308-7e62-4b26-98b2-d4bd4d45f415',
          tenantId,
          tenantName: 'Example tenant',
          expiresAt: new Date(Date.now() + 60_000)
            .toISOString()
            .replace('Z', '+00:00'),
        },
      ]);
      return;
    }
    if (request.url === '/internal/v1/authz/resolve') {
      if (mode === 'denied') {
        json(response, {
          tenantId,
          decision: 'deny',
          permissions: [],
        });
        return;
      }
      json(response, {
        tenantId: mode === 'wrong-tenant' ? otherTenantId : input.tenantId,
        tenantStatus: 'active',
        membershipStatus: 'active',
        roles: ['member'],
        permissions: mode === 'no-app' ? [] : ['profile.app.access'],
        apps: mode === 'no-app' ? [] : ['profile'],
        version: 42,
        resolvedAt: new Date().toISOString().replace('Z', '+00:00'),
      });
      return;
    }
    response.writeHead(404).end();
  });
  await new Promise((resolve) => server.listen(0, 'localhost', resolve));
  origin = `http://localhost:${server.address().port}`;
  config = loadAuthConfig({
    OIDC_ISSUER: origin,
    OIDC_CLIENT_ID: 'excellore-profile',
    OIDC_CLIENT_SECRET: 'browser-secret',
    OIDC_SCOPES: 'openid profile api-profile',
    APP_BASE_URL: 'http://profile.excellore.localhost:3001',
    SESSION_COOKIE_NAME: 'excellore-dev-profile',
    SESSION_STORE_URL: 'redis://localhost:6379',
    SESSION_KEY_PREFIX: 'profile:sess:',
    SESSION_ENCRYPTION_KEY: Buffer.alloc(32).toString('base64url'),
    EXCELLORE_INSECURE_LOCAL_DEV: 'true',
    NODE_ENV: 'test',
  });
  access = loadAccessClientConfig({
    ACCESS_INTERNAL_URL: origin,
    AUTHZ_CLIENT_ID: 'svc-profile-authz',
    AUTHZ_CLIENT_SECRET: 'machine-secret',
  });
  client = await createAccessClient(config, access);
});

after(async () => {
  await new Promise((resolve) => server.close(resolve));
});

test('Access client uses one machine token and POST bodies for user-scoped contracts', async () => {
  const [tenant, tenants, snapshot, invitations] = await Promise.all([
    client.resolveSlug(userId, 'example'),
    client.listTenants(userId),
    client.resolve(userId, tenantId),
    client.listPendingInvitations(userId),
  ]);
  assert.equal(tokenCalls, 1);
  assert.equal(tenant.tenantId, tenantId);
  assert.equal(tenants.length, 1);
  assert.equal(snapshot.decision, 'allow');
  assert.equal(invitations.length, 1);
  assert.deepEqual(
    calls.map((call) => call.url).sort(),
    [
      '/internal/v1/tenants:resolve-slug',
      '/internal/v1/users/tenants:list',
      '/internal/v1/authz/resolve',
      '/internal/v1/users/invitations:list',
    ].sort(),
  );
  assert.ok(calls.every((call) => call.method === 'POST'));
  assert.ok(calls.every((call) => JSON.parse(call.body).userId === userId));
  assert.ok(calls.every((call) => !call.url.includes(userId)));
});

test('requireTenant fails closed for missing session, non-member, denied and wrong-tenant responses', async () => {
  const sid = 'a'.repeat(43);
  const request = new Request(`${config.APP_BASE_URL}/t/example`, {
    headers: { Cookie: `${config.SESSION_COOKIE_NAME}=${sid}` },
  });
  const store = {
    get: async (value) => (value === sid ? { sub: userId } : null),
  };
  await assert.rejects(
    requireSession(new Request(request.url), config, store),
    (error) =>
      error instanceof SessionRequiredError &&
      error.redirectTo === '/auth/login?returnTo=%2Ft%2Fexample',
  );
  const result = await requireTenant(
    request,
    'example',
    config,
    store,
    client,
    'profile',
  );
  assert.equal(result.tenantId, tenantId);
  assert.deepEqual(result.permissions, ['profile.app.access']);

  mode = 'nonmember';
  await assert.rejects(
    requireTenant(request, 'example', config, store, client, 'profile'),
    (error) => error instanceof TenantAccessError && error.status === 404,
  );
  mode = 'denied';
  await assert.rejects(
    requireTenant(request, 'example', config, store, client, 'profile'),
    (error) => error instanceof TenantAccessError && error.status === 404,
  );
  mode = 'no-app';
  await assert.rejects(
    requireTenant(request, 'example', config, store, client, 'profile'),
    (error) => error instanceof TenantAccessError && error.status === 403,
  );
  mode = 'wrong-tenant';
  await assert.rejects(
    requireTenant(request, 'example', config, store, client, 'profile'),
    (error) => error instanceof AccessUnavailableError && error.status === 503,
  );
  mode = 'wrong-slug';
  await assert.rejects(
    requireTenant(request, 'example', config, store, client, 'profile'),
    (error) => error instanceof AccessUnavailableError && error.status === 503,
  );
  mode = 'unavailable';
  await assert.rejects(
    requireTenant(request, 'example', config, store, client, 'profile'),
    (error) => error instanceof AccessUnavailableError && error.status === 503,
  );
  mode = 'allow';
});
