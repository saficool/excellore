import assert from 'node:assert/strict';
import test from 'node:test';
import { neonConnectionString } from './neon-database-config.mjs';

const env = {
  ACCESS_NEON_DIRECT_HOST: 'ep-access-direct.example.neon.tech',
  ACCESS_NEON_POOLED_HOST: 'ep-access-pooler.example.neon.tech',
  ACCESS_NEON_DATABASE: 'neondb',
  ACCESS_APP_PASSWORD: "app'password",
  ACCESS_PUBLISHER_PASSWORD: 'publisher-password',
  ACCESS_WORKER_PASSWORD: 'worker-password',
  PROFILE_NEON_POOLED_HOST: 'ep-profile-pooler.example.neon.tech',
  PROFILE_NEON_DATABASE: 'neondb',
  PROFILE_WORKER_PASSWORD: 'worker-password',
};

test('request and publisher roles use a secure pooled Neon endpoint', () => {
  const value = neonConnectionString(env, 'access', 'app');

  assert.match(value, /Host='ep-access-pooler\.example\.neon\.tech'/u);
  assert.match(value, /Username='access_app'/u);
  assert.match(value, /Password='app''password'/u);
  assert.match(value, /SSL Mode='VerifyFull'/u);
  assert.match(value, /Channel Binding='Require'/u);
  assert.match(value, /Maximum Pool Size='20'/u);
});

test('Access worker uses the direct endpoint for session advisory locks', () => {
  const value = neonConnectionString(env, 'access', 'worker');

  assert.match(value, /Host='ep-access-direct\.example\.neon\.tech'/u);
  assert.match(value, /Maximum Pool Size='10'/u);
});

test('Profile worker can use transaction pooling', () => {
  const value = neonConnectionString(env, 'profile', 'worker');

  assert.match(value, /Host='ep-profile-pooler\.example\.neon\.tech'/u);
});

test('host fields reject URLs and embedded credentials', () => {
  assert.throws(
    () =>
      neonConnectionString(
        { ...env, ACCESS_NEON_POOLED_HOST: 'postgres://user:secret@host' },
        'access',
        'app',
      ),
    /hostname only/u,
  );
});
