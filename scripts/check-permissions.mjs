import { readFileSync, readdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseDocument } from 'yaml';

const roles = new Set(['owner', 'admin', 'member', 'viewer', 'auditor']);
const catalogKeys = new Set(['app', 'permissions']);
const permissionKeys = new Set([
  'key',
  'description',
  'privileged',
  'ownerOnly',
  'deprecated',
  'systemRoles',
]);

function plainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function validatePermissionCatalog(source, expectedApp) {
  const document = parseDocument(source, { uniqueKeys: true });
  if (document.errors.length)
    throw new Error('Invalid permission catalog YAML');
  const catalog = document.toJS({ maxAliasCount: 0 });
  if (
    !plainObject(catalog) ||
    Object.keys(catalog).some((key) => !catalogKeys.has(key)) ||
    catalog.app !== expectedApp ||
    !/^[a-z][a-z0-9-]{0,39}$/.test(expectedApp) ||
    !Array.isArray(catalog.permissions) ||
    catalog.permissions.length < 1 ||
    catalog.permissions.length > 200
  )
    throw new Error('Invalid permission catalog');

  const seen = new Set();
  for (const [index, permission] of catalog.permissions.entries()) {
    if (
      !plainObject(permission) ||
      Object.keys(permission).some((key) => !permissionKeys.has(key)) ||
      typeof permission.key !== 'string' ||
      permission.key.length > 120 ||
      !new RegExp(
        `^${expectedApp.replaceAll('-', '\\-')}\\.[a-z][a-z0-9]*(?:\\.[a-z][a-z0-9]*)+$`,
      ).test(permission.key) ||
      seen.has(permission.key) ||
      typeof permission.description !== 'string' ||
      !permission.description.trim() ||
      permission.description.length > 240 ||
      ['privileged', 'ownerOnly', 'deprecated'].some(
        (key) => key in permission && typeof permission[key] !== 'boolean',
      ) ||
      ('systemRoles' in permission && !Array.isArray(permission.systemRoles))
    )
      throw new Error(`Invalid permission definition at index ${index}`);

    const systemRoles = permission.systemRoles ?? [];
    if (
      systemRoles.length > roles.size ||
      new Set(systemRoles).size !== systemRoles.length ||
      systemRoles.some((role) => !roles.has(role)) ||
      (!permission.deprecated && !systemRoles.includes('owner')) ||
      (permission.ownerOnly && systemRoles.some((role) => role !== 'owner'))
    )
      throw new Error('Invalid system role grants');
    seen.add(permission.key);
  }
  if (!seen.has(`${expectedApp}.app.access`))
    throw new Error('Missing app access permission');
  return catalog;
}

export function validateCatalogTransition(previous, current) {
  const now = new Set(current.permissions.map((permission) => permission.key));
  if (
    previous.permissions.some(
      (permission) => !now.has(permission.key) && !permission.deprecated,
    )
  )
    throw new Error('Permission removal requires a prior deprecated release');
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  const services = readdirSync('services', { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name);
  const baseRef = process.env.PERMISSIONS_BASE_REF;
  if (baseRef && !/^0+$/.test(baseRef))
    execFileSync('git', ['rev-parse', '--verify', `${baseRef}^{commit}`], {
      stdio: 'ignore',
    });
  let count = 0;
  for (const app of services) {
    const path = join('services', app, 'permissions.yaml');
    let source;
    try {
      source = readFileSync(path, 'utf8');
    } catch {
      source = null;
    }
    if (baseRef && !/^0+$/.test(baseRef)) {
      let previousSource;
      try {
        previousSource = execFileSync(
          'git',
          ['show', `${baseRef}:${path.replaceAll('\\', '/')}`],
          {
            encoding: 'utf8',
            stdio: ['ignore', 'pipe', 'ignore'],
          },
        );
      } catch {
        previousSource = null;
      }
      if (previousSource !== null && source === null)
        throw new Error('Existing permission catalog cannot be removed');
      if (previousSource !== null && source !== null)
        validateCatalogTransition(
          validatePermissionCatalog(previousSource, app),
          validatePermissionCatalog(source, app),
        );
    }
    if (source === null) continue;
    validatePermissionCatalog(source, app);
    count++;
    process.stdout.write(`Validated ${path}\n`);
  }
  if (!count) throw new Error('No permission catalogs found');
}
