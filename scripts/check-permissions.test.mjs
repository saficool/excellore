import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { stringify } from 'yaml';
import {
  validateCatalogTransition,
  validatePermissionCatalog,
} from './check-permissions.mjs';

const source = readFileSync('services/access/permissions.yaml', 'utf8');

test('Access catalog validates with explicit system role grants', () => {
  const catalog = validatePermissionCatalog(source, 'access');
  assert.equal(catalog.permissions.length, 19);
  assert.deepEqual(
    catalog.permissions.find(
      (permission) => permission.key === 'access.supportgrant.manage',
    ).systemRoles,
    ['owner'],
  );
  const tenantExport = catalog.permissions.find(
    (permission) => permission.key === 'access.tenant.export',
  );
  assert.deepEqual(tenantExport.systemRoles, ['owner']);
  assert.equal(tenantExport.ownerOnly, true);
  assert.equal(tenantExport.privileged, true);
});

test('removal requires a prior release marking the permission deprecated', () => {
  const previous = validatePermissionCatalog(source, 'access');
  const current = structuredClone(previous);
  current.permissions = current.permissions.filter(
    (p) => p.key !== 'access.role.manage',
  );
  assert.throws(() => validateCatalogTransition(previous, current));
  const deprecated = structuredClone(previous);
  deprecated.permissions.find(
    (p) => p.key === 'access.role.manage',
  ).deprecated = true;
  assert.doesNotThrow(() => validateCatalogTransition(deprecated, current));
});

test('catalog validation rejects duplicate keys, bad roles, unsafe owner grants, and absent app gate', () => {
  const mutations = [
    (catalog) => {
      catalog.permissions[1].key = 'access.app.access';
    },
    (catalog) => {
      catalog.permissions[0].systemRoles = ['owner', 'stranger'];
    },
    (catalog) => {
      catalog.permissions.find((p) => p.ownerOnly).systemRoles = [
        'owner',
        'admin',
      ];
    },
    (catalog) => {
      catalog.permissions[0].systemRoles = ['admin', 'member'];
    },
    (catalog) => {
      catalog.permissions[0].key = 'access.app.open';
    },
    (catalog) => {
      catalog.app = 'profile';
    },
  ];
  for (const mutate of mutations) {
    const catalog = structuredClone(
      validatePermissionCatalog(source, 'access'),
    );
    mutate(catalog);
    assert.throws(() =>
      validatePermissionCatalog(stringify(catalog), 'access'),
    );
  }
});
