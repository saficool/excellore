import assert from 'node:assert/strict';
import test from 'node:test';
import {
  forwardedHeaders,
  normalizeHost,
  upstreamPort,
} from './local-https-proxy.mjs';

test('routes only the four exact local hostnames', () => {
  assert.equal(upstreamPort('excellore.localhost'), 3000);
  assert.equal(upstreamPort('PROFILE.EXCELLORE.LOCALHOST:443'), 3001);
  assert.equal(upstreamPort('access.excellore.localhost'), 3002);
  assert.equal(upstreamPort('platform.excellore.localhost'), 3003);
  assert.equal(upstreamPort('auth.excellore.localhost'), 8080);
  assert.equal(upstreamPort('attacker.example'), undefined);
  assert.equal(
    upstreamPort('auth.excellore.localhost.attacker.example'),
    undefined,
  );
});

test('replaces untrusted forwarding metadata', () => {
  const headers = forwardedHeaders(
    {
      host: 'excellore.localhost',
      forwarded: 'for=attacker',
      'x-forwarded-for': '203.0.113.7',
      'x-forwarded-host': 'attacker.example',
      'x-forwarded-port': '80',
      'x-forwarded-proto': 'http',
    },
    normalizeHost('excellore.localhost:443'),
  );
  assert.equal(headers.host, 'excellore.localhost');
  assert.equal(headers.forwarded, undefined);
  assert.equal(headers['x-forwarded-for'], '127.0.0.1');
  assert.equal(headers['x-forwarded-host'], 'excellore.localhost');
  assert.equal(headers['x-forwarded-port'], '443');
  assert.equal(headers['x-forwarded-proto'], 'https');
});
