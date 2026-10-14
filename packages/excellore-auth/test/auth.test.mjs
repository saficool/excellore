import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { createServer } from 'node:http';
import { after, before, test } from 'node:test';
import { exportJWK, generateKeyPair, SignJWT } from 'jose';
import {
  createAuthHandlers,
  createOidcClient,
  getAccessToken,
  loadAuthConfig,
  ValkeySessionStore,
} from '../dist/index.js';

class FakeValkey {
  strings = new Map();
  sets = new Map();
  expiries = new Map();

  async get(key) {
    if (this.expiries.has(key) && this.expiries.get(key) <= Date.now()) {
      this.strings.delete(key);
      this.sets.delete(key);
      this.expiries.delete(key);
    }
    return this.strings.get(key) ?? null;
  }
  async getdel(key) {
    const value = await this.get(key);
    this.strings.delete(key);
    return value;
  }
  async set(key, value, ...args) {
    if (args.includes('NX') && this.strings.has(key)) return null;
    this.strings.set(key, value);
    const ex = args.indexOf('EX');
    const px = args.indexOf('PX');
    if (ex !== -1)
      this.expiries.set(key, Date.now() + Number(args[ex + 1]) * 1000);
    if (px !== -1) this.expiries.set(key, Date.now() + Number(args[px + 1]));
    return 'OK';
  }
  async del(key) {
    return Number(this.strings.delete(key) || this.sets.delete(key));
  }
  async sadd(key, value) {
    const set = this.sets.get(key) ?? new Set();
    set.add(value);
    this.sets.set(key, set);
    return 1;
  }
  async srem(key, value) {
    return Number(this.sets.get(key)?.delete(value) ?? false);
  }
  async smembers(key) {
    return [...(this.sets.get(key) ?? [])];
  }
  async expire(key, seconds) {
    this.expiries.set(key, Date.now() + seconds * 1000);
    return 1;
  }
  multi() {
    const commands = [];
    const transaction = {};
    for (const name of ['set', 'del', 'sadd', 'srem', 'expire']) {
      transaction[name] = (...args) => {
        commands.push([name, args]);
        return transaction;
      };
    }
    transaction.exec = async () =>
      Promise.all(commands.map(([name, args]) => this[name](...args)));
    return transaction;
  }
  async eval(script, count, key, ...args) {
    assert.equal(count, 1);
    if (script.includes("redis.call('SET', KEYS[1], ARGV[2]")) {
      if ((await this.get(key)) !== args[0]) return 0;
      await this.set(key, args[1], 'EX', Number(args[2]));
      return 1;
    }
    if (script.includes("redis.call('PEXPIRE'")) {
      if ((await this.get(key)) !== args[0]) return 0;
      this.expiries.set(key, Date.now() + Number(args[1]));
      return 1;
    }
    if ((await this.get(key)) !== args[0]) return 0;
    return this.del(key);
  }
}

let server;
let base;
let signingKey;
let publicJwk;
let config;
let oidcClient;
let store;
let handlers;
let challenge;
let nonce;
let code;
let refreshCalls = 0;
let logoutCalls = 0;
let lastLogoutBody;
let tamperNextIdToken = false;
let nextAcr = '2';
const secret = randomBytes(32).toString('base64url');
const clientSecret = randomBytes(32).toString('base64url');
const userId = randomBytes(16).toString('hex');
let nextSub = userId;
const keycloakSid = randomBytes(16).toString('hex');

async function sign(claims, audience = 'excellore-profile') {
  return new SignJWT(claims)
    .setProtectedHeader({ alg: 'RS256', kid: 'test-key' })
    .setIssuer(base)
    .setAudience(audience)
    .setIssuedAt()
    .setExpirationTime('10m')
    .sign(signingKey);
}

function json(response, body) {
  response.setHeader('Content-Type', 'application/json');
  response.end(JSON.stringify(body));
}

async function readRequest(request) {
  let body = '';
  for await (const chunk of request) body += chunk.toString();
  return body;
}

before(async () => {
  const pair = await generateKeyPair('RS256');
  signingKey = pair.privateKey;
  publicJwk = {
    ...(await exportJWK(pair.publicKey)),
    kid: 'test-key',
    alg: 'RS256',
    use: 'sig',
  };
  server = createServer(async (request, response) => {
    if (request.url === '/.well-known/openid-configuration') {
      json(response, {
        issuer: base,
        authorization_endpoint: `${base}/authorize`,
        token_endpoint: `${base}/token`,
        jwks_uri: `${base}/jwks`,
        end_session_endpoint: `${base}/logout`,
        response_types_supported: ['code'],
        grant_types_supported: ['authorization_code', 'refresh_token'],
        subject_types_supported: ['public'],
        id_token_signing_alg_values_supported: ['RS256'],
        token_endpoint_auth_methods_supported: ['client_secret_post'],
        code_challenge_methods_supported: ['S256'],
      });
      return;
    }
    if (request.url === '/jwks') {
      json(response, { keys: [publicJwk] });
      return;
    }
    if (request.url === '/token') {
      const body = new URLSearchParams(await readRequest(request));
      if (
        body.get('client_id') !== 'excellore-profile' ||
        body.get('client_secret') !== clientSecret
      ) {
        response.writeHead(401).end();
        return;
      }
      if (body.get('grant_type') === 'authorization_code') {
        const digest = createHash('sha256')
          .update(body.get('code_verifier') ?? '')
          .digest('base64url');
        if (body.get('code') !== code || digest !== challenge) {
          response.writeHead(400).end();
          return;
        }
        let idToken = await sign({
          sub: nextSub,
          sid: keycloakSid,
          nonce,
          acr: nextAcr,
        });
        if (tamperNextIdToken) {
          tamperNextIdToken = false;
          const parts = idToken.split('.');
          parts[2] = `${parts[2][0] === 'A' ? 'B' : 'A'}${parts[2].slice(1)}`;
          idToken = parts.join('.');
        }
        json(response, {
          access_token: randomBytes(32).toString('base64url'),
          token_type: 'Bearer',
          expires_in: 30,
          refresh_token: randomBytes(32).toString('base64url'),
          id_token: idToken,
        });
        return;
      }
      if (body.get('grant_type') === 'refresh_token') {
        refreshCalls += 1;
        json(response, {
          access_token: randomBytes(32).toString('base64url'),
          token_type: 'Bearer',
          expires_in: 300,
          refresh_token: randomBytes(32).toString('base64url'),
        });
        return;
      }
    }
    if (request.url === '/logout' && request.method === 'POST') {
      logoutCalls += 1;
      lastLogoutBody = await readRequest(request);
      response.writeHead(204).end();
      return;
    }
    response.writeHead(404).end();
  });
  await new Promise((resolve) => server.listen(0, 'localhost', resolve));
  base = `http://localhost:${server.address().port}`;
  config = loadAuthConfig({
    OIDC_ISSUER: base,
    OIDC_CLIENT_ID: 'excellore-profile',
    OIDC_CLIENT_SECRET: clientSecret,
    OIDC_SCOPES: 'openid profile email api-profile',
    APP_BASE_URL: 'http://profile.excellore.localhost:3001',
    SESSION_COOKIE_NAME: 'excellore-dev-profile',
    SESSION_STORE_URL: 'redis://localhost:6379',
    SESSION_KEY_PREFIX: 'profile:sess:',
    SESSION_ENCRYPTION_KEY: secret,
    EXCELLORE_INSECURE_LOCAL_DEV: 'true',
    NODE_ENV: 'test',
  });
  oidcClient = await createOidcClient(config);
  store = new ValkeySessionStore(
    new FakeValkey(),
    config.SESSION_KEY_PREFIX,
    secret,
  );
  handlers = createAuthHandlers(config, store, oidcClient);
});

after(async () => {
  await new Promise((resolve) => server.close(resolve));
});

test('config requires secure production origins and host cookies', () => {
  assert.throws(() => loadAuthConfig({ ...config, NODE_ENV: 'production' }));
  assert.equal(
    loadAuthConfig({
      ...config,
      APP_BASE_URL: 'https://profile.example.test',
      OIDC_ISSUER: 'https://auth.example.test',
      SESSION_COOKIE_NAME: '__Host-excellore-profile',
      EXCELLORE_INSECURE_LOCAL_DEV: undefined,
      NODE_ENV: 'production',
    }).SESSION_COOKIE_NAME,
    '__Host-excellore-profile',
  );
});

test('step-up binds the same subject and account actions are allow-listed', async () => {
  const existing = await store.create({
    kcSid: 'kc-step-up',
    sub: userId,
    acr: '1',
    accessToken: 'opaque',
    accessTokenExpiresAt: Math.floor(Date.now() / 1000) + 300,
    refreshToken: 'opaque',
    idToken: 'opaque',
  });
  const headers = { Cookie: `excellore-dev-profile=${existing.sid}` };
  const step = await handlers.stepUp(
    new Request(`${config.APP_BASE_URL}/auth/step-up?returnTo=%2Faccount`, {
      headers,
    }),
  );
  assert.equal(step.status, 302);
  const stepUrl = new URL(step.headers.get('location'));
  assert.equal(stepUrl.searchParams.get('acr_values'), '2');
  assert.equal(stepUrl.searchParams.get('prompt'), 'login');
  challenge = stepUrl.searchParams.get('code_challenge');
  nonce = stepUrl.searchParams.get('nonce');
  code = randomBytes(32).toString('base64url');
  nextAcr = '1';
  try {
    const callback = await handlers.callback(
      new Request(
        `${config.APP_BASE_URL}/auth/callback?state=${stepUrl.searchParams.get('state')}&code=${code}`,
        { headers: { Cookie: step.headers.get('set-cookie').split(';')[0] } },
      ),
    );
    assert.equal(callback.status, 400, 'A low-ACR step-up must fail');
    assert.ok(
      await store.get(existing.sid),
      'Failed step-up must retain the old session',
    );
  } finally {
    nextAcr = '2';
  }
  const rejected = await handlers.accountAction(
    new Request(
      `${config.APP_BASE_URL}/auth/account-action?action=DELETE_ACCOUNT`,
      { headers },
    ),
  );
  assert.equal(rejected.status, 400);
  const accepted = await handlers.accountAction(
    new Request(
      `${config.APP_BASE_URL}/auth/account-action?action=CONFIGURE_TOTP`,
      { headers },
    ),
  );
  assert.equal(accepted.status, 302);
  assert.equal(
    new URL(accepted.headers.get('location')).searchParams.get('kc_action'),
    'CONFIGURE_TOTP',
  );
  const actionUrl = new URL(accepted.headers.get('location'));
  challenge = actionUrl.searchParams.get('code_challenge');
  nonce = actionUrl.searchParams.get('nonce');
  code = randomBytes(32).toString('base64url');
  nextSub = 'another-user';
  try {
    const callback = await handlers.callback(
      new Request(
        `${config.APP_BASE_URL}/auth/callback?state=${actionUrl.searchParams.get('state')}&code=${code}`,
        {
          headers: { Cookie: accepted.headers.get('set-cookie').split(';')[0] },
        },
      ),
    );
    assert.equal(
      callback.status,
      400,
      'Switching subjects during account action must fail',
    );
  } finally {
    nextSub = userId;
  }
});

test('login and callback bind PKCE, nonce, state, and browser flow cookie', async () => {
  const origin = config.APP_BASE_URL;
  const redirect = await handlers.login(
    new Request(`${origin}/auth/login?returnTo=%2Ft%2Ftenant-a`),
  );
  assert.equal(redirect.status, 302);
  assert.equal(redirect.headers.get('cache-control'), 'no-store');
  assert.match(redirect.headers.get('set-cookie'), /HttpOnly; SameSite=Lax/);
  const authUrl = new URL(redirect.headers.get('location'));
  assert.equal(authUrl.searchParams.get('code_challenge_method'), 'S256');
  challenge = authUrl.searchParams.get('code_challenge');
  nonce = authUrl.searchParams.get('nonce');
  code = randomBytes(32).toString('base64url');
  const flowCookie = redirect.headers.get('set-cookie').split(';')[0];
  const callbackUrl = `${origin}/auth/callback?state=${authUrl.searchParams.get('state')}&code=${code}`;
  assert.equal((await handlers.callback(new Request(callbackUrl))).status, 400);
  const callback = await handlers.callback(
    new Request(callbackUrl, { headers: { Cookie: flowCookie } }),
  );
  assert.equal(callback.status, 302);
  assert.equal(callback.headers.get('location'), `${origin}/t/tenant-a`);
  const sessionCookie = callback.headers
    .getSetCookie()
    .find((item) => item.startsWith('excellore-dev-profile='));
  assert.ok(sessionCookie);
  const sessionId = sessionCookie.split(';')[0].split('=')[1];
  assert.equal(
    (
      await handlers.callback(
        new Request(callbackUrl, { headers: { Cookie: flowCookie } }),
      )
    ).status,
    400,
  );
  assert.equal(
    (
      await handlers.session(
        new Request(`${origin}/auth/session`, {
          headers: { Cookie: `excellore-dev-profile=${sessionId}` },
        }),
      )
    ).status,
    200,
  );
  const summary = await handlers.session(
    new Request(`${origin}/auth/session`, {
      headers: { Cookie: `excellore-dev-profile=${sessionId}` },
    }),
  );
  assert.equal((await summary.json()).authenticated, true);
  assert.equal(
    store.redis.strings
      .get(`profile:sess:${sessionId}`)
      .includes('accessToken'),
    false,
  );

  const [tokenA, tokenB] = await Promise.all([
    getAccessToken(sessionId, store, oidcClient),
    getAccessToken(sessionId, store, oidcClient),
  ]);
  assert.equal(tokenA, tokenB);
  assert.equal(refreshCalls, 1);
  assert.equal(await getAccessToken(sessionId, store, oidcClient), tokenA);

  const invalid = await handlers.login(
    new Request(`${origin}/auth/login?returnTo=https:%2F%2Fevil.example`),
  );
  assert.equal(invalid.status, 400);
  const forged = await handlers.login(
    new Request(`${origin}/auth/login?returnTo=%2F%2Fevil.example`),
  );
  assert.equal(forged.status, 400);

  const logoutToken = await sign({
    jti: randomBytes(16).toString('hex'),
    sid: keycloakSid,
    events: { 'http://schemas.openid.net/event/backchannel-logout': {} },
  });
  const backchannel = new Request(`${origin}/auth/backchannel-logout`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ logout_token: logoutToken }),
  });
  assert.equal(
    (await handlers.backchannelLogout(backchannel.clone())).status,
    200,
  );
  assert.equal(await store.get(sessionId), null);
  assert.equal(
    (await handlers.backchannelLogout(new Request(backchannel))).status,
    200,
  );
});

test('callback rejects a tampered ID token signature', async () => {
  const origin = config.APP_BASE_URL;
  const started = await handlers.login(new Request(`${origin}/auth/login`));
  const authUrl = new URL(started.headers.get('location'));
  challenge = authUrl.searchParams.get('code_challenge');
  nonce = authUrl.searchParams.get('nonce');
  code = randomBytes(32).toString('base64url');
  tamperNextIdToken = true;
  const callback = await handlers.callback(
    new Request(
      `${origin}/auth/callback?state=${authUrl.searchParams.get('state')}&code=${code}`,
      { headers: { Cookie: started.headers.get('set-cookie').split(';')[0] } },
    ),
  );
  assert.equal(callback.status, 400);
});

test('back-channel logout rejects wrong audience and nonce', async () => {
  const origin = config.APP_BASE_URL;
  for (const [claims, aud] of [
    [
      {
        jti: randomBytes(16).toString('hex'),
        sid: keycloakSid,
        events: { 'http://schemas.openid.net/event/backchannel-logout': {} },
      },
      'other-client',
    ],
    [
      {
        jti: randomBytes(16).toString('hex'),
        sid: keycloakSid,
        nonce: 'forbidden',
        events: { 'http://schemas.openid.net/event/backchannel-logout': {} },
      },
      'excellore-profile',
    ],
  ]) {
    const token = await sign(claims, aud);
    const request = new Request(`${origin}/auth/backchannel-logout`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ logout_token: token }),
    });
    assert.equal((await handlers.backchannelLogout(request)).status, 400);
  }
});

test('logout requires same origin and posts ID token only in server-side body', async () => {
  const session = await store.create({
    kcSid: keycloakSid,
    sub: userId,
    acr: '2',
    accessToken: randomBytes(32).toString('base64url'),
    accessTokenExpiresAt: Math.floor(Date.now() / 1000) + 300,
    refreshToken: randomBytes(32).toString('base64url'),
    idToken: await sign({ sub: userId, sid: keycloakSid }),
  });
  const url = `${config.APP_BASE_URL}/auth/logout`;
  const cookie = `excellore-dev-profile=${session.sid}`;
  assert.equal(
    (
      await handlers.logout(
        new Request(url, { method: 'POST', headers: { Cookie: cookie } }),
      )
    ).status,
    400,
  );
  const response = await handlers.logout(
    new Request(url, {
      method: 'POST',
      headers: { Cookie: cookie, Origin: config.APP_BASE_URL },
    }),
  );
  assert.equal(response.status, 303);
  assert.equal(response.headers.get('location'), `${config.APP_BASE_URL}/`);
  assert.equal(logoutCalls, 1);
  assert.equal(
    new URLSearchParams(lastLogoutBody).get('id_token_hint'),
    session.idToken,
  );
  assert.equal(
    response.headers.get('location').includes(session.idToken),
    false,
  );
  assert.equal(await store.get(session.sid), null);
});
