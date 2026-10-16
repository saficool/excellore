import 'server-only';
import { cookies, headers } from 'next/headers';
import Valkey from 'iovalkey';
import {
  canonicalBffRequest,
  createAccessClient,
  createAuthHandlers,
  createOidcClient,
  loadAccessClientConfig,
  loadAuthConfig,
  requireSession,
  requireTenant,
  ValkeySessionStore,
} from '@excellore/auth';
import { tenantLinksForApp, visibleTenantApps } from '@excellore/config';
import { loadAppCatalog } from '@excellore/config/server';
import { resolve } from 'node:path';

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
  return { config, store, handlers: createAuthHandlers(config, store, client) };
}

async function getResources() {
  resources ??= createResources().catch((error) => {
    resources = undefined;
    throw error;
  });
  return resources;
}

async function getAccess() {
  accessPromise ??= createAccessClient(
    (await getResources()).config,
    loadAccessClientConfig(),
  ).catch((error) => {
    accessPromise = undefined;
    throw error;
  });
  return accessPromise;
}

function catalog() {
  return loadAppCatalog(
    resolve(process.cwd(), '../../infra/platform/app-catalog.yaml'),
  );
}

function navigation() {
  const apps = catalog().apps;
  const profile = apps.find((app) => app.id === 'profile');
  const access = apps.find((app) => app.id === 'access');
  if (!profile?.userLevelUrl || !access)
    throw new Error('Shell app catalog is incomplete');
  return {
    accountHref: profile.userLevelUrl,
    invitationBase: new URL('/invitations/accept', access.urlTemplate).href,
  };
}

function identity(session: {
  displayName?: string | undefined;
  email?: string | undefined;
}) {
  const displayName =
    session.displayName ?? session.email?.split('@')[0] ?? 'My account';
  return {
    displayName,
    ...(session.email ? { email: session.email } : {}),
  };
}

export function getPublicNavigation() {
  return navigation();
}

export async function hasShellSessionCookie() {
  const config = loadAuthConfig();
  return (await cookies()).has(config.SESSION_COOKIE_NAME);
}

async function pageRequest(path: string) {
  const { config } = await getResources();
  const requestHeaders = await headers();
  return new Request(new URL(path, config.APP_BASE_URL), {
    headers: { Cookie: requestHeaders.get('cookie') ?? '' },
  });
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

export async function getPickerData() {
  const { config, store } = await getResources();
  const session = await requireSession(await pageRequest('/'), config, store);
  const access = await getAccess();
  const [tenants, invitations] = await Promise.all([
    access.listTenants(session.sub),
    access.listPendingInvitations(session.sub),
  ]);
  return {
    tenants: tenantLinksForApp(catalog(), 'shell', tenants),
    invitations,
    identity: identity(session),
    ...navigation(),
  };
}

export async function getLauncherData(slug: string) {
  const { config, store } = await getResources();
  const request = await pageRequest(`/t/${slug}`);
  const access = await getAccess();
  const tenant = await requireTenant(
    request,
    slug,
    config,
    store,
    access,
    'shell',
  );
  const session = await requireSession(request, config, store);
  const [tenants, invitations] = await Promise.all([
    access.listTenants(session.sub),
    access.listPendingInvitations(session.sub),
  ]);
  const appCatalog = catalog();
  return {
    tenant,
    tenants: tenantLinksForApp(appCatalog, 'shell', tenants),
    apps: visibleTenantApps(appCatalog, tenant, slug),
    invitations,
    identity: identity(session),
    ...navigation(),
  };
}
