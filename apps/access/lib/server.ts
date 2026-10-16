import 'server-only';
import { headers } from 'next/headers';
import Valkey from 'iovalkey';
import {
  canonicalBffRequest,
  createAccessClient,
  createAuthHandlers,
  createOidcClient,
  getAccessToken,
  loadAccessClientConfig,
  loadAuthConfig,
  requireSession,
  requireTenant,
  ValkeySessionStore,
} from '@excellore/auth';
import { tenantLinksForApp, visibleTenantApps } from '@excellore/config';
import { loadAppCatalog } from '@excellore/config/server';
import { resolve } from 'node:path';

const appId: string = 'access';
let resources: Promise<Awaited<ReturnType<typeof createResources>>> | undefined;
let accessPromise: ReturnType<typeof createAccessClient> | undefined;

async function createResources() {
  const config = loadAuthConfig();
  const redis = new Valkey(config.SESSION_STORE_URL, { lazyConnect: true });
  const store = new ValkeySessionStore(
    redis,
    config.SESSION_KEY_PREFIX,
    config.SESSION_ENCRYPTION_KEY,
  );
  const client = await createOidcClient(config);
  return {
    config,
    store,
    client,
    handlers: createAuthHandlers(config, store, client),
  };
}

async function getResources() {
  resources ??= createResources().catch((error) => {
    resources = undefined;
    throw error;
  });
  return resources;
}

export async function getAuthHandlers() {
  return (await getResources()).handlers;
}

export async function canonicalRequest(request: Request) {
  return canonicalBffRequest(
    request,
    (await getResources()).config.APP_BASE_URL,
  );
}

export async function getPageSession(path: string) {
  const { config, store } = await getResources();
  const requestHeaders = await headers();
  return requireSession(
    new Request(new URL(path, config.APP_BASE_URL), {
      headers: { Cookie: requestHeaders.get('cookie') ?? '' },
    }),
    config,
    store,
  );
}

export async function getTenantDependencies() {
  const { config, store, client } = await getResources();
  accessPromise ??= createAccessClient(config, loadAccessClientConfig()).catch(
    (error) => {
      accessPromise = undefined;
      throw error;
    },
  );
  const access = await accessPromise;
  return {
    config,
    store,
    oidcClient: client,
    access,
    app: appId,
    apiBaseUrl: process.env.API_BASE_URL ?? '',
  };
}

export async function getApiDependencies() {
  const { config, store, client } = await getResources();
  return {
    config,
    store,
    oidcClient: client,
    app: appId,
    apiBaseUrl: process.env.API_BASE_URL ?? '',
  };
}

export async function getPlatformRoles(): Promise<string[] | null> {
  const { config, store, client } = await getResources();
  const requestHeaders = await headers();
  const request = new Request(new URL('/platform', config.APP_BASE_URL), {
    headers: { Cookie: requestHeaders.get('cookie') ?? '' },
  });
  const session = await requireSession(request, config, store);
  const token = await getAccessToken(session.sid, store, client);
  const base = process.env.API_BASE_URL;
  if (!base) throw new Error('Access API base URL is unavailable');
  const response = await fetch(new URL('/v1/platform/me', base), {
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
    cache: 'no-store',
    redirect: 'manual',
    signal: AbortSignal.timeout(15_000),
  });
  if (response.status === 403) return null;
  if (!response.ok) throw new Error('Platform role check failed');
  const body: unknown = await response.json();
  if (
    !body ||
    typeof body !== 'object' ||
    !('roles' in body) ||
    !Array.isArray(body.roles) ||
    !body.roles.every((role: unknown) => typeof role === 'string')
  )
    throw new Error('Platform role response is invalid');
  return body.roles;
}

export async function getTenantPageData(slug: string) {
  const { config, store, access } = await getTenantDependencies();
  const requestHeaders = await headers();
  const request = new Request(new URL(`/t/${slug}`, config.APP_BASE_URL), {
    headers: { Cookie: requestHeaders.get('cookie') ?? '' },
  });
  const tenant = await requireTenant(
    request,
    slug,
    config,
    store,
    access,
    appId,
  );
  const session = await requireSession(request, config, store);
  const allTenants = (await access.listTenants(session.sub)).filter(
    (item) => appId === 'shell' || item.apps.includes(appId),
  );
  const catalog = loadAppCatalog(
    resolve(process.cwd(), '../../infra/platform/app-catalog.yaml'),
  );
  return {
    tenant,
    tenants: tenantLinksForApp(catalog, appId, allTenants),
    apps: visibleTenantApps(catalog, tenant, slug),
  };
}
