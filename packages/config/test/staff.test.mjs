import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { parsePlatformStaff } from '../dist/index.js';
import { loadPlatformStaff } from '@excellore/config/server';

test('code-owned platform staff manifest is a bounded read-only roster', () => {
  const path = fileURLToPath(
    new URL('../../../infra/keycloak/platform-staff.yaml', import.meta.url),
  );
  const staff = loadPlatformStaff(path);
  assert.deepEqual(
    staff.find((item) => item.username === 'penny.platform')?.roles,
    ['platform-operator'],
  );
  assert.throws(() =>
    parsePlatformStaff('staff:\n  - username: alice\n    roles: [owner]'),
  );
  assert.throws(() =>
    parsePlatformStaff(
      'staff:\n  - username: alice\n    roles: [platform-support]\n  - username: alice\n    roles: [platform-auditor]',
    ),
  );
  assert.throws(() =>
    parsePlatformStaff(
      'staff:\n  - username: alice\n    roles: [platform-support, platform-support]',
    ),
  );
});
