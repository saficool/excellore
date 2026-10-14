import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import {
  parseAppCatalog,
  tenantAppUrl,
  tenantLinksForApp,
  visibleTenantApps,
} from '../dist/index.js';
import { loadAppCatalog } from '@excellore/config/server';

const path = fileURLToPath(
  new URL('../../../infra/platform/app-catalog.yaml', import.meta.url),
);
const production = {
  PROFILE_ORIGIN: 'https://profile.example.test',
  ACCESS_ORIGIN: 'https://access.example.test',
  NODE_ENV: 'production',
};

test('catalog loads from YAML, resolves origins, and shows only allowed tenant apps', () => {
  const catalog = loadAppCatalog(path, production);
  assert.deepEqual(
    catalog.apps.map((app) => app.id),
    ['profile', 'access'],
  );
  assert.equal(
    tenantAppUrl(catalog.apps[0], 'example-co'),
    'https://profile.example.test/t/example-co',
  );
  assert.equal(
    catalog.apps[0].userLevelUrl,
    'https://profile.example.test/account',
  );
  const links = visibleTenantApps(
    catalog,
    {
      apps: ['profile', 'access'],
      permissions: ['profile.app.access'],
    },
    'example-co',
  );
  assert.deepEqual(
    links.map((app) => app.id),
    ['profile'],
  );
  const tenantLinks = tenantLinksForApp(catalog, 'profile', [
    { slug: 'example-co', name: 'Example Co', status: 'active' },
    { slug: 'suspended-co', name: 'Suspended Co', status: 'suspended' },
  ]);
  assert.deepEqual(tenantLinks, [
    {
      id: 'example-co',
      name: 'Example Co',
      href: 'https://profile.example.test/t/example-co',
    },
  ]);
  assert.deepEqual(
    tenantLinksForApp(catalog, 'shell', [
      { slug: 'example-co', name: 'Example Co', status: 'active' },
    ]).map((link) => link.href),
    ['/t/example-co'],
  );
  assert.throws(() => tenantAppUrl(catalog.apps[0], '../other'));
});

test('catalog rejects duplicate IDs, mismatched permissions, and unsafe origins', () => {
  const source = `apps:
  - id: profile
    name: Profile
    description: Profile app
    icon: user
    originEnv: PROFILE_ORIGIN
    urlTemplate: '{origin}/t/{slug}'
    appPermission: profile.app.access
    core: true
    order: 10
`;
  assert.throws(() => parseAppCatalog(source + source.slice(6), production));
  assert.throws(() =>
    parseAppCatalog(
      source.replace('profile.app.access', 'access.app.access'),
      production,
    ),
  );
  assert.throws(() =>
    parseAppCatalog(source, {
      ...production,
      PROFILE_ORIGIN: 'javascript:alert(1)',
    }),
  );
  assert.throws(() =>
    parseAppCatalog(source, {
      ...production,
      PROFILE_ORIGIN: 'http://profile.example.test',
    }),
  );
  assert.throws(() =>
    parseAppCatalog(source, {
      ...production,
      PROFILE_ORIGIN: 'https://user:pass@profile.example.test',
    }),
  );
  const local = parseAppCatalog(source, {
    ...production,
    NODE_ENV: 'development',
    EXCELLORE_INSECURE_LOCAL_DEV: 'true',
    PROFILE_ORIGIN: 'http://profile.excellore.localhost:3001',
  });
  assert.equal(
    tenantAppUrl(local.apps[0], 'example'),
    'http://profile.excellore.localhost:3001/t/example',
  );
});
