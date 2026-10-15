import assert from 'node:assert/strict';
import { test } from 'node:test';
import { lastUsedTenant } from '../lib/navigation.ts';

const tenants = [
  { id: 'alpha', name: 'Alpha', href: '/t/alpha' },
  { id: 'beta', name: 'Beta', href: '/t/beta' },
];

test('last used preference resolves only against the fresh authorized tenant list', () => {
  assert.equal(lastUsedTenant(tenants, 'beta')?.href, '/t/beta');
  assert.equal(lastUsedTenant(tenants, 'other'), null);
  assert.equal(lastUsedTenant([], 'beta'), null);
});
