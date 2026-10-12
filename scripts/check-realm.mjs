import assert from 'node:assert/strict';
import { createHmac, createHash, randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';

const env = Object.fromEntries(
  readFileSync(new URL('../.env', import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => {
      const separator = line.indexOf('=');
      return [line.slice(0, separator), line.slice(separator + 1)];
    }),
);
const issuer = 'http://auth.excellore.localhost:8080/realms/excellore';
const adminBase = 'http://auth.excellore.localhost:8080/admin/realms/excellore';

async function token(form, realm = 'excellore') {
  const response = await fetch(
    `http://auth.excellore.localhost:8080/realms/${realm}/protocol/openid-connect/token`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(form),
    },
  );
  return { status: response.status, data: await response.json() };
}

function claims(jwt) {
  return JSON.parse(Buffer.from(jwt.split('.')[1], 'base64url').toString());
}

const adminLogin = await token(
  {
    grant_type: 'password',
    client_id: 'admin-cli',
    username: 'admin',
    password: env.KEYCLOAK_ADMIN_PASSWORD,
  },
  'master',
);
assert.equal(adminLogin.status, 200, 'local admin login');
const adminToken = adminLogin.data.access_token;
async function adminGet(path) {
  const response = await fetch(`${adminBase}${path}`, {
    headers: { authorization: `Bearer ${adminToken}` },
  });
  assert.equal(response.status, 200, `admin GET ${path}`);
  return response.json();
}

const realm = await adminGet('');
assert.equal(realm.realm, 'excellore');
assert.equal(realm.eventsEnabled, true);
assert.equal(realm.adminEventsEnabled, true);
assert.deepEqual(realm.eventsListeners ?? [], []);
assert.equal(realm.registrationAllowed, false);
assert.equal(realm.accessTokenLifespan, 300);
assert.equal(realm.ssoSessionIdleTimeout, 1800);
assert.equal(realm.revokeRefreshToken, true);
assert.equal(realm.refreshTokenMaxReuse, 0);
assert.equal(realm.bruteForceProtected, true);
assert.equal(realm.verifyEmail, true);
assert.equal(realm.loginTheme, 'excellore');
assert.ok(realm.passwordPolicy.includes('length(12)'));
assert.ok(
  realm.passwordPolicy.includes('passwordBlacklist(excellore-common.txt)'),
);

const clients = await adminGet('/clients');
const clientScopes = await adminGet('/client-scopes');
const realmRoleMappers = [
  ...clients.flatMap((client) => client.protocolMappers ?? []),
  ...clientScopes.flatMap((scope) => scope.protocolMappers ?? []),
].filter(
  (mapper) => mapper.protocolMapper === 'oidc-usermodel-realm-role-mapper',
);
assert.deepEqual(
  realmRoleMappers.map((mapper) => mapper.name),
  ['platform-roles'],
);
const secretKeys = {
  'excellore-shell': 'SHELL_BFF_SECRET',
  'excellore-profile': 'PROFILE_BFF_SECRET',
  'excellore-access': 'ACCESS_BFF_SECRET',
  'worker-profile': 'WORKER_PROFILE_SECRET',
  'worker-access': 'WORKER_ACCESS_SECRET',
  'svc-shell-authz': 'SVC_SHELL_AUTHZ_SECRET',
  'svc-profile-authz': 'SVC_PROFILE_AUTHZ_SECRET',
  'svc-catalog-sync': 'SVC_CATALOG_SYNC_SECRET',
  'svc-access-admin': 'SVC_ACCESS_ADMIN_SECRET',
  'svc-profile-identity': 'SVC_PROFILE_IDENTITY_SECRET',
};
const expected = [
  'excellore-shell',
  'excellore-profile',
  'excellore-access',
  'api-profile',
  'api-access',
  'worker-profile',
  'worker-access',
  'svc-shell-authz',
  'svc-profile-authz',
  'svc-catalog-sync',
  'svc-access-admin',
  'svc-profile-identity',
];
for (const clientId of expected) {
  const client = clients.find((candidate) => candidate.clientId === clientId);
  assert.ok(client, `missing ${clientId}`);
  assert.equal(client.fullScopeAllowed, false, `${clientId} full scope`);
  assert.equal(
    client.directAccessGrantsEnabled,
    false,
    `${clientId} password grant`,
  );
  assert.equal(client.implicitFlowEnabled, false, `${clientId} implicit grant`);
  assert.equal(client.standardFlowEnabled, clientId.startsWith('excellore-'));
  assert.deepEqual(client.webOrigins ?? [], [], `${clientId} web origins`);
  if (clientId.startsWith('excellore-')) {
    const app = clientId.slice('excellore-'.length).toUpperCase();
    assert.deepEqual(client.redirectUris, [
      `${env[`${app}_ORIGIN`]}/auth/callback`,
    ]);
    assert.equal(client.attributes['pkce.code.challenge.method'], 'S256');
    assert.equal(client.attributes['default.acr.values'], '2');
    assert.equal(
      client.attributes['post.logout.redirect.uris'],
      `${env[`${app}_ORIGIN`]}/`,
    );
    assert.equal(
      client.attributes['backchannel.logout.url'],
      `${env[`${app}_ORIGIN`]}/auth/backchannel-logout`,
    );
    assert.equal(
      client.attributes['backchannel.logout.session.required'],
      'true',
    );
  }
  const passwordGrant = await token({
    client_id: clientId,
    client_secret: env[secretKeys[clientId]] ?? 'invalid',
    grant_type: 'password',
    username: 'nonexistent.grant-test',
    password: env.DEV_USER_PASSWORD,
  });
  assert.notEqual(
    passwordGrant.status,
    200,
    `${clientId} accepted password grant`,
  );
}

for (const app of ['shell', 'profile', 'access']) {
  const clientId = `excellore-${app}`;
  const callback = `${env[`${app.toUpperCase()}_ORIGIN`]}/auth/callback`;
  for (const redirectUri of [
    `${env[`${app.toUpperCase()}_ORIGIN`]}/auth/unregistered`,
    'http://unregistered.excellore.localhost/auth/callback',
    `${env[`${app.toUpperCase()}_ORIGIN`]}/auth/*`,
  ]) {
    const auth = new URL(`${issuer}/protocol/openid-connect/auth`);
    auth.search = new URLSearchParams({
      client_id: clientId,
      redirect_uri: redirectUri,
      response_type: 'code',
      scope: 'openid',
      code_challenge: createHash('sha256')
        .update('test-verifier')
        .digest('base64url'),
      code_challenge_method: 'S256',
    });
    const response = await fetch(auth, { redirect: 'manual' });
    assert.equal(response.status, 400, `${clientId} accepted invalid redirect`);
  }
  const implicit = new URL(`${issuer}/protocol/openid-connect/auth`);
  implicit.search = new URLSearchParams({
    client_id: clientId,
    redirect_uri: callback,
    response_type: 'token',
    scope: 'openid',
  });
  const response = await fetch(implicit, { redirect: 'manual' });
  assert.notEqual(response.status, 200, `${clientId} accepted implicit flow`);
  assert.ok(!response.headers.get('location')?.includes('access_token='));
}

const machine = await token({
  client_id: 'svc-profile-authz',
  client_secret: env.SVC_PROFILE_AUTHZ_SECRET,
  grant_type: 'client_credentials',
});
assert.equal(machine.status, 200, 'svc-profile-authz token');
const machineClaims = claims(machine.data.access_token);
const serviceAccountClient = clients.find(
  (client) => client.clientId === 'svc-profile-authz',
);
const serviceAccountUser = await adminGet(
  `/clients/${serviceAccountClient.id}/service-account-user`,
);
const apiAccessClient = clients.find(
  (client) => client.clientId === 'api-access',
);
const accessBffClient = clients.find(
  (client) => client.clientId === 'excellore-access',
);
assert.ok(
  (
    await adminGet(
      `/users/${serviceAccountUser.id}/role-mappings/clients/${apiAccessClient.id}`,
    )
  ).some((role) => role.name === 'access.authz.resolve'),
);
assert.deepEqual(
  (await adminGet(`/clients/${accessBffClient.id}/scope-mappings/realm`))
    .map((role) => role.name)
    .sort(),
  [
    'platform-admin',
    'platform-auditor',
    'platform-operator',
    'platform-security',
    'platform-support',
  ],
);
for (const client of clients.filter(
  (candidate) => candidate.clientId !== 'excellore-access',
)) {
  assert.deepEqual(
    await adminGet(`/clients/${client.id}/scope-mappings/realm`),
    [],
    `${client.clientId} unexpectedly has realm role scope mappings`,
  );
}
assert.ok([machineClaims.aud].flat().includes('api-access'));
assert.ok(machineClaims.roles?.includes('access.authz.resolve'));
assert.equal(machineClaims.platform_roles, undefined);
assert.ok(!Object.keys(machineClaims).some((key) => /tenant/i.test(key)));

const adminMachine = await token({
  client_id: 'svc-access-admin',
  client_secret: env.SVC_ACCESS_ADMIN_SECRET,
  grant_type: 'client_credentials',
});
assert.equal(adminMachine.status, 200, 'svc-access-admin token');
const adminAccess = await fetch(`${adminBase}/users?max=1`, {
  headers: { authorization: `Bearer ${adminMachine.data.access_token}` },
});
assert.equal(
  adminAccess.status,
  200,
  'svc-access-admin cannot read Keycloak users',
);

class BrowserSession {
  cookies = new Map();

  async request(url, options = {}) {
    const headers = new Headers(options.headers);
    if (this.cookies.size) {
      headers.set(
        'cookie',
        [...this.cookies].map(([key, value]) => `${key}=${value}`).join('; '),
      );
    }
    const response = await fetch(url, {
      ...options,
      headers,
      redirect: 'manual',
    });
    for (const cookie of response.headers.getSetCookie()) {
      const [pair] = cookie.split(';');
      const separator = pair.indexOf('=');
      const key = pair.slice(0, separator);
      const value = pair.slice(separator + 1);
      if (value) this.cookies.set(key, value);
      else this.cookies.delete(key);
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
  // Keycloak's HmacOTP uses the stored secret's UTF-8 bytes as the HMAC key.
  const digest = createHmac('sha1', Buffer.from(secret, 'utf8'))
    .update(counter)
    .digest();
  const offset = digest.at(-1) & 15;
  return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1000000)
    .toString()
    .padStart(6, '0');
}

async function userToken(
  session,
  clientId,
  secret,
  redirectUri,
  username,
  acr,
) {
  const verifier = randomBytes(32).toString('base64url');
  const challenge = createHash('sha256').update(verifier).digest('base64url');
  const authUrl = new URL(`${issuer}/protocol/openid-connect/auth`);
  authUrl.search = new URLSearchParams({
    client_id: clientId,
    redirect_uri: redirectUri,
    response_type: 'code',
    scope: 'openid',
    state: randomBytes(8).toString('hex'),
    code_challenge: challenge,
    code_challenge_method: 'S256',
    ...(acr ? { acr_values: acr } : {}),
  });
  let response = await session.request(authUrl);
  let otpRequested = false;
  if (response.status === 200) {
    const html = await response.text();
    const action = formAction(html);
    if (/name="otp"/i.test(html)) {
      otpRequested = true;
      response = await session.request(action, {
        method: 'POST',
        headers: { 'content-type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({ otp: otp(env.DEV_OTP_SECRET) }),
      });
    } else {
      response = await session.request(action, {
        method: 'POST',
        headers: { 'content-type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          username,
          password: env.DEV_USER_PASSWORD,
        }),
      });
    }
  }
  if (response.status === 200) {
    otpRequested = true;
    const action = formAction(await response.text());
    response = await session.request(action, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ otp: otp(env.DEV_OTP_SECRET) }),
    });
  }
  if (response.status === 200) {
    const html = await response.text();
    const errors = await adminGet('/events?type=LOGIN_ERROR&max=3');
    console.log('Unexpected Keycloak page:', {
      otpForm: /name="otp"/i.test(html),
      passwordForm: /name="password"/i.test(html),
      errorPage: /id="kc-error-message"/i.test(html),
      challenge: /name="code"/i.test(html),
      errors: errors.map((event) => event.error),
    });
  }
  assert.equal(response.status, 302, `${clientId} login did not redirect`);
  const callback = new URL(response.headers.get('location'));
  assert.equal(callback.origin + callback.pathname, redirectUri);
  const code = callback.searchParams.get('code');
  assert.ok(code, `${clientId} login has no authorization code`);
  const result = await token({
    client_id: clientId,
    client_secret: secret,
    grant_type: 'authorization_code',
    redirect_uri: redirectUri,
    code,
    code_verifier: verifier,
  });
  assert.equal(result.status, 200, `${clientId} code exchange`);
  return { claims: claims(result.data.access_token), otpRequested };
}

const profileSession = new BrowserSession();
// Seed OTP credentials reject reuse within a 30-second TOTP window.
await delay(31000 - (Date.now() % 30000));
const profile = await userToken(
  profileSession,
  'excellore-profile',
  env.PROFILE_BFF_SECRET,
  `${env.PROFILE_ORIGIN}/auth/callback`,
  'olivia.owner',
);
assert.ok([profile.claims.aud].flat().includes('api-profile'));
assert.equal(profile.claims.acr, '2');
assert.equal(profile.otpRequested, true);
assert.equal(profile.claims.roles, undefined);
assert.equal(profile.claims.platform_roles, undefined);
assert.ok(!Object.keys(profile.claims).some((key) => /tenant/i.test(key)));

const accessSession = new BrowserSession();
const access = await userToken(
  accessSession,
  'excellore-access',
  env.ACCESS_BFF_SECRET,
  `${env.ACCESS_ORIGIN}/auth/callback`,
  'mia.member',
);
assert.ok([access.claims.aud].flat().includes('api-access'));
assert.equal(access.claims.acr, '2');
assert.equal(access.otpRequested, true);
assert.deepEqual(access.claims.platform_roles, []);
assert.equal(access.claims.roles, undefined);
assert.ok(!Object.keys(access.claims).some((key) => /tenant/i.test(key)));

const staffSession = new BrowserSession();
const staff = await userToken(
  staffSession,
  'excellore-access',
  env.ACCESS_BFF_SECRET,
  `${env.ACCESS_ORIGIN}/auth/callback`,
  'penny.platform',
);
assert.deepEqual(staff.claims.platform_roles, ['platform-operator']);
assert.equal(staff.claims.acr, '2');

const lowerSession = new BrowserSession();
const lower = await userToken(
  lowerSession,
  'excellore-access',
  env.ACCESS_BFF_SECRET,
  `${env.ACCESS_ORIGIN}/auth/callback`,
  'nora.nomember',
  '1',
);
assert.equal(lower.claims.acr, '1');
assert.equal(lower.otpRequested, false);

const steppedUp = await userToken(
  lowerSession,
  'excellore-access',
  env.ACCESS_BFF_SECRET,
  `${env.ACCESS_ORIGIN}/auth/callback`,
  'nora.nomember',
  '2',
);
assert.equal(steppedUp.otpRequested, true, 'higher ACR did not require OTP');
assert.equal(steppedUp.claims.acr, '2');

console.log(
  'Realm import, machine and browser claims, and MFA step-up verified.',
);
