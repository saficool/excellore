import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

test('Platform app exposes only its platform API proxy', async () => {
  const route = await readFile(
    new URL('../app/api/platform/[...path]/route.ts', import.meta.url),
    'utf8',
  );
  assert.match(route, /scope: 'platform'/);
  assert.doesNotMatch(route, /scope: 'tenant'|requireTenant/);
});

test('Platform app keeps tokens in the shared server-side BFF', async () => {
  const server = await readFile(
    new URL('../lib/server.ts', import.meta.url),
    'utf8',
  );
  assert.match(server, /ValkeySessionStore/);
  assert.match(server, /getAccessToken/);
  assert.doesNotMatch(server, /localStorage|sessionStorage/);
});
