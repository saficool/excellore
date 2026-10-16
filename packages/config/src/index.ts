import { parseDocument } from 'yaml';
import { z } from 'zod';

export const id = '@excellore/config';

const platformRole = z.enum([
  'platform-admin',
  'platform-operator',
  'platform-support',
  'platform-auditor',
  'platform-security',
]);
const platformStaffSchema = z
  .object({
    staff: z.array(
      z
        .object({
          username: z.string().regex(/^[a-z0-9]+(?:[.-][a-z0-9]+)*$/),
          roles: z.array(platformRole).nonempty(),
        })
        .strict(),
    ),
  })
  .strict();
export type PlatformStaff = z.infer<typeof platformStaffSchema>['staff'];

export function parsePlatformStaff(source: string): PlatformStaff {
  const document = parseDocument(source, { uniqueKeys: true, strict: true });
  if (document.errors.length > 0)
    throw new Error('Platform staff YAML is invalid');
  const parsed = platformStaffSchema.parse(document.toJS() as unknown);
  const usernames = new Set<string>();
  for (const entry of parsed.staff) {
    if (
      usernames.has(entry.username) ||
      new Set(entry.roles).size !== entry.roles.length
    )
      throw new Error('Duplicate platform staff assignment');
    usernames.add(entry.username);
  }
  return parsed.staff;
}

const appId = z.string().regex(/^[a-z][a-z0-9-]*$/);
const tenantSlug = z.string().regex(/^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$/);
const catalogSourceSchema = z
  .object({
    apps: z
      .array(
        z
          .object({
            id: appId,
            name: z.string().min(1).max(80),
            description: z.string().min(1).max(240),
            icon: z.string().regex(/^[a-z][a-z0-9-]*$/),
            originEnv: z.string().regex(/^[A-Z][A-Z0-9_]*_ORIGIN$/),
            urlTemplate: z.literal('{origin}/t/{slug}'),
            userLevelUrl: z
              .string()
              .regex(/^\{origin\}\/[a-z][a-z0-9/-]*$/)
              .optional(),
            appPermission: z.string().regex(/^[a-z][a-z0-9-]*\.app\.access$/),
            core: z.boolean(),
            order: z.number().int().nonnegative(),
          })
          .strict(),
      )
      .min(1),
  })
  .strict();

export interface CatalogApp {
  id: string;
  name: string;
  description: string;
  icon: string;
  urlTemplate: string;
  userLevelUrl?: string;
  appPermission: string;
  core: boolean;
  order: number;
}

export interface AppCatalog {
  apps: readonly CatalogApp[];
}

export interface AppLink {
  id: string;
  name: string;
  icon: string;
  href: string;
}

export interface TenantLink {
  id: string;
  name: string;
  href: string;
}

function checkedOrigin(
  value: string | undefined,
  env: Record<string, string | undefined>,
): string {
  if (!value) throw new Error('App catalog origin is missing');
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error('App catalog origin is invalid');
  }
  const local =
    env.EXCELLORE_INSECURE_LOCAL_DEV === 'true' &&
    (env.NODE_ENV === 'development' || env.NODE_ENV === 'test') &&
    (url.hostname === 'localhost' || url.hostname.endsWith('.localhost'));
  if (
    (url.protocol !== 'https:' && !(local && url.protocol === 'http:')) ||
    url.username ||
    url.password ||
    url.pathname !== '/' ||
    url.search ||
    url.hash
  )
    throw new Error('App catalog origin must be a trusted origin URL');
  return url.origin;
}

export function parseAppCatalog(
  source: string,
  env: Record<string, string | undefined>,
): AppCatalog {
  const document = parseDocument(source, { uniqueKeys: true, strict: true });
  if (document.errors.length > 0)
    throw new Error('App catalog YAML is invalid');
  const parsed = catalogSourceSchema.parse(document.toJS() as unknown);
  const seen = new Set<string>();
  const apps = parsed.apps.map((item): CatalogApp => {
    if (seen.has(item.id)) throw new Error('Duplicate app catalog ID');
    seen.add(item.id);
    if (item.appPermission !== `${item.id}.app.access`)
      throw new Error('App catalog permission does not match its app');
    const origin = checkedOrigin(env[item.originEnv], env);
    return {
      id: item.id,
      name: item.name,
      description: item.description,
      icon: item.icon,
      urlTemplate: item.urlTemplate.replace('{origin}', origin),
      ...(item.userLevelUrl
        ? { userLevelUrl: item.userLevelUrl.replace('{origin}', origin) }
        : {}),
      appPermission: item.appPermission,
      core: item.core,
      order: item.order,
    };
  });
  return {
    apps: apps.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id)),
  };
}

export function tenantAppUrl(app: CatalogApp, slug: string): string {
  tenantSlug.parse(slug);
  return app.urlTemplate.replace('{slug}', slug);
}

export function visibleTenantApps(
  catalog: AppCatalog,
  access: { apps: readonly string[]; permissions: readonly string[] },
  slug: string,
): AppLink[] {
  tenantSlug.parse(slug);
  return catalog.apps
    .filter(
      (app) =>
        access.apps.includes(app.id) &&
        access.permissions.includes(app.appPermission),
    )
    .map((app) => ({
      id: app.id,
      name: app.name,
      icon: app.icon,
      href: tenantAppUrl(app, slug),
    }));
}

export function tenantLinksForApp(
  catalog: AppCatalog,
  appId: string,
  tenants: readonly {
    slug: string;
    name: string;
    status: string;
  }[],
): TenantLink[] {
  const app =
    appId === 'shell' ? null : catalog.apps.find((item) => item.id === appId);
  if (appId !== 'shell' && !app) throw new Error('Unknown app catalog ID');
  return tenants
    .filter((tenant) => tenant.status === 'active')
    .map((tenant) => ({
      id: tenant.slug,
      name: tenant.name,
      href: app
        ? tenantAppUrl(app, tenant.slug)
        : `/t/${tenantSlug.parse(tenant.slug)}`,
    }));
}
