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
const origin = env.ACCESS_ORIGIN;
assert.ok(
  origin?.endsWith('.localhost:3002') &&
    env.DEV_USER_PASSWORD &&
    env.DEV_OTP_SECRET,
);

class Browser {
  cookies = new Map();
  async request(url, options = {}) {
    const source = new URL(url).origin;
    const jar = this.cookies.get(source) ?? new Map();
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
      const separator = pair.indexOf('=');
      if (pair.slice(separator + 1))
        jar.set(pair.slice(0, separator), pair.slice(separator + 1));
      else jar.delete(pair.slice(0, separator));
    }
    this.cookies.set(source, jar);
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

async function loginAs(browser, username) {
  let response = await browser.request(
    `${origin}/auth/login?returnTo=%2Ft%2Facme%2Fmy-access`,
  );
  assert.equal(response.status, 302, 'Access BFF login');
  response = await browser.request(response.headers.get('location'));
  assert.equal(response.status, 200, 'Keycloak login');
  let html = await response.text();
  response = await browser.request(formAction(html), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ username, password: env.DEV_USER_PASSWORD }),
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
  assert.equal(response.status, 302, 'Access BFF session');
  assert.equal(
    new URL(response.headers.get('location')).pathname,
    '/t/acme/my-access',
  );
}

async function api(browser, path, method = 'GET', body) {
  const response = await browser.request(`${origin}${path}`, {
    method,
    headers: {
      Origin: origin,
      ...(method !== 'GET' ? { 'X-Excellore-CSRF': '1' } : {}),
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  return response;
}

const browser = new Browser();
await loginAs(browser, 'olivia.owner');
let response;
let html;

response = await browser.request(`${origin}/auth/session`);
assert.equal(response.status, 200);
assert.equal((await response.json()).authenticated, true);
response = await browser.request(`${origin}/t/acme/my-access`);
assert.equal(response.status, 200, 'owner console page');
html = await response.text();
assert.match(html, /My access/);
assert.match(html, /Members/);
assert.match(html, /Roles/);
assert.doesNotMatch(html, /(?:access_token|refresh_token|id_token)/i);
response = await browser.request(`${origin}/t/acme/roles`);
assert.equal(response.status, 200, 'roles page');
response = await browser.request(`${origin}/t/nonmember/my-access`);
assert.equal(response.status, 404, 'foreign tenant page');

for (const path of [
  'me/access',
  'members',
  'roles',
  'permissions',
  'role-assignments',
  'apps',
  'audit-events',
  'support-grants',
  'settings',
]) {
  response = await browser.request(`${origin}/api/t/acme/${path}`);
  assert.equal(response.status, 200, `${path} BFF proxy`);
  assert.equal(response.headers.get('cache-control'), 'no-store');
}
response = await browser.request(`${origin}/api/t/nonmember/members`);
assert.equal(response.status, 404, 'foreign tenant API');
response = await browser.request(`${origin}/api/t/acme/roles`, {
  method: 'POST',
  headers: { Origin: origin, 'Content-Type': 'application/json' },
  body: '{}',
});
assert.equal(response.status, 403, 'CSRF guard');

const realm = JSON.parse(
  await readFile(new URL('../infra/keycloak/realm-dev.json', import.meta.url)),
);
const invitedEmail = realm.users.find(
  (user) => user.username === 'nora.nomember',
)?.email;
assert.ok(invitedEmail, 'development invitee');
response = await api(browser, '/api/t/acme/roles');
assert.equal(response.status, 200);
const roles = await response.json();
const memberRole = roles.find((role) => role.name === 'member');
assert.ok(memberRole);
response = await api(browser, '/api/t/acme/members');
assert.equal(response.status, 200);
let member = (await response.json()).find(
  (item) => item.email === invitedEmail && item.status === 'active',
);
const invitee = new Browser();
if (!member) {
  response = await api(browser, '/api/t/acme/invitations', 'POST', {
    email: invitedEmail,
    roleIds: [memberRole.id],
    groupIds: [],
    reason: 'console_smoke',
  });
  assert.equal(response.status, 202, 'owner invitation');
  response = await api(browser, '/api/t/acme/invitations');
  assert.equal(response.status, 200);
  const invitation = (await response.json()).find(
    (item) => item.email === invitedEmail && item.status === 'pending',
  );
  assert.ok(invitation, 'pending invitation');
  let oneTimeCode;
  for (let attempt = 0; attempt < 10 && !oneTimeCode; attempt++) {
    const mailbox = await (
      await fetch('http://127.0.0.1:8025/api/v1/messages')
    ).json();
    for (const message of mailbox.messages) {
      if (message.Subject !== 'Your Excellore invitation') continue;
      const detail = await (
        await fetch(
          `http://127.0.0.1:8025/api/v1/message/${encodeURIComponent(message.ID)}`,
        )
      ).json();
      if (!detail.Text?.includes(invitation.id)) continue;
      oneTimeCode = detail.Text.match(
        /Enter this one-time invitation code: (\S+)/,
      )?.[1];
      break;
    }
    if (!oneTimeCode) await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  assert.ok(oneTimeCode, 'one-time code delivered to Mailpit');
  await loginAs(invitee, 'nora.nomember');
  response = await api(invitee, '/api/me/invitations/accept', 'POST', {
    invitationId: invitation.id,
    oneTimeCode,
  });
  assert.equal(response.status, 200, 'member invitation acceptance');
  response = await api(browser, '/api/t/acme/members');
  assert.equal(response.status, 200);
  member = (await response.json()).find(
    (item) => item.email === invitedEmail && item.status === 'active',
  );
  assert.ok(member, 'accepted member');
} else {
  await loginAs(invitee, 'nora.nomember');
}

response = await api(invitee, '/api/t/acme/audit-events');
assert.equal(response.status, 403, 'member cannot call audit API directly');
response = await invitee.request(`${origin}/t/acme/audit`);
assert.equal(response.status, 404, 'unavailable audit screen is hidden');

const roleName = `wp08-audit-${Date.now().toString(36)}`;
response = await api(browser, '/api/t/acme/roles', 'POST', {
  name: roleName,
  description: 'Local console acceptance check',
  permissions: ['access.audit.read'],
});
assert.equal(response.status, 201, 'owner creates custom role with MFA');
const { roleId } = await response.json();
response = await api(browser, '/api/t/acme/role-assignments', 'POST', {
  userId: member.userId,
  roleId,
  expiresAt: null,
  reason: 'console_smoke',
});
assert.equal(response.status, 201, 'owner assigns custom role with MFA');
const { assignmentId } = await response.json();
let granted = false;
for (let attempt = 0; attempt < 24; attempt++) {
  response = await api(invitee, '/api/t/acme/audit-events');
  if (response.status === 200) {
    granted = true;
    break;
  }
  await new Promise((resolve) => setTimeout(resolve, 5000));
}
assert.ok(granted, 'permission takes effect in same browser session');
response = await invitee.request(`${origin}/t/acme/audit`);
assert.equal(response.status, 200, 'newly granted screen is available');
response = await api(
  browser,
  `/api/t/acme/role-assignments/${assignmentId}`,
  'DELETE',
  { reason: 'console_smoke_cleanup' },
);
assert.equal(response.status, 204, 'assignment cleanup');
response = await api(browser, `/api/t/acme/roles/${roleId}`, 'DELETE');
assert.equal(response.status, 204, 'custom role cleanup');
process.stdout.write(
  'Access console login, tenant gate, invitation, role grant, live permission, CSRF and token isolation passed.\n',
);
