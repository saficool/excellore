import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import Valkey from 'iovalkey';
import { ValkeySessionStore } from '../dist/index.js';

test('Valkey encrypts sessions, enforces expiry and shared refresh locks, and revokes by Keycloak sid', async () => {
  const redis = new Valkey(
    process.env.EXCELLORE_VALKEY_TEST_URL ?? 'redis://127.0.0.1:6379',
  );
  const prefix = `wp04-${randomBytes(6).toString('hex')}:sess:`;
  const encryptionKey = randomBytes(32).toString('base64url');
  const storeA = new ValkeySessionStore(redis, prefix, encryptionKey);
  const storeB = new ValkeySessionStore(redis, prefix, encryptionKey);
  try {
    await redis.ping();
    const flowId = randomBytes(32).toString('base64url');
    const flow = {
      state: 'opaque-state',
      nonce: 'opaque-nonce',
      verifier: 'opaque-verifier',
      returnTo: '/',
    };
    await storeA.createFlow(flowId, flow);
    assert.deepEqual(await storeB.consumeFlow(flowId), flow);
    assert.equal(await storeA.consumeFlow(flowId), null);

    const token = randomBytes(32).toString('base64url');
    const session = await storeA.create({
      kcSid: randomBytes(16).toString('hex'),
      sub: randomBytes(16).toString('hex'),
      acr: '2',
      accessToken: token,
      accessTokenExpiresAt: Math.floor(Date.now() / 1000) + 300,
      refreshToken: randomBytes(32).toString('base64url'),
      idToken: randomBytes(32).toString('base64url'),
    });
    const raw = await redis.get(`${prefix}${session.sid}`);
    assert.ok(raw);
    assert.equal(raw.includes(token), false);
    assert.equal(raw.includes(session.sub), false);
    assert.ok((await redis.ttl(`${prefix}${session.sid}`)) <= 1800);
    assert.equal((await storeB.get(session.sid)).sub, session.sub);

    assert.equal(await storeA.acquireRefreshLock(session.sid, 'first'), true);
    assert.equal(await storeB.acquireRefreshLock(session.sid, 'second'), false);
    await storeB.releaseRefreshLock(session.sid, 'second');
    assert.equal(await storeA.extendRefreshLock(session.sid, 'first'), true);
    await storeA.releaseRefreshLock(session.sid, 'first');
    assert.equal(await storeB.acquireRefreshLock(session.sid, 'second'), true);
    await storeB.releaseRefreshLock(session.sid, 'second');

    await storeB.deleteByKeycloakSession(session.kcSid);
    assert.equal(await storeA.get(session.sid), null);
  } finally {
    const keys = await redis.keys(`${prefix}*`);
    if (keys.length) await redis.del(...keys);
    redis.disconnect();
  }
});

test('global sign-out revokes other app sessions and permits a later login', async () => {
  const redis = new Valkey(
    process.env.EXCELLORE_VALKEY_TEST_URL ?? 'redis://127.0.0.1:6379',
  );
  const suffix = randomBytes(6).toString('hex');
  const shell = new ValkeySessionStore(
    redis,
    `shell-${suffix}:sess:`,
    randomBytes(32).toString('base64url'),
  );
  const profile = new ValkeySessionStore(
    redis,
    `profile-${suffix}:sess:`,
    randomBytes(32).toString('base64url'),
  );
  const sub = randomBytes(16).toString('hex');
  const newSession = () => ({
    kcSid: randomBytes(16).toString('hex'),
    sub,
    acr: '2',
    accessToken: randomBytes(32).toString('base64url'),
    accessTokenExpiresAt: Math.floor(Date.now() / 1000) + 300,
    refreshToken: randomBytes(32).toString('base64url'),
    idToken: randomBytes(32).toString('base64url'),
  });
  try {
    await redis.ping();
    const shellSession = await shell.create(newSession());
    const profileSession = await profile.create(newSession());
    assert.ok(await profile.get(profileSession.sid));
    await shell.revokeSubjectGlobally(sub);
    assert.equal(await shell.get(shellSession.sid), null);
    assert.equal(await profile.get(profileSession.sid), null);
    const fresh = await profile.create(newSession());
    assert.ok(await profile.get(fresh.sid));
  } finally {
    const keys = await redis.keys(`*${suffix}*`);
    if (keys.length) await redis.del(...keys);
    await redis.del(
      `excellore:logout:${createHash('sha256').update(sub).digest('hex')}`,
    );
    redis.disconnect();
  }
});
