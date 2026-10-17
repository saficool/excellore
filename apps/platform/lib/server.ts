import 'server-only';
import { headers } from 'next/headers';
import Valkey from 'iovalkey';
import {
  canonicalBffRequest,
  createAuthHandlers,
  createOidcClient,
  getAccessToken,
  loadAuthConfig,
  requireSession,
  ValkeySessionStore,
} from '@excellore/auth';

let resources: Promise<Awaited<ReturnType<typeof createResources>>> | undefined;

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

export async function getApiDependencies() {
  const { config, store, client } = await getResources();
  return {
    config,
    store,
    oidcClient: client,
    app: 'platform',
    apiBaseUrl: process.env.API_BASE_URL ?? '',
  };
}

export async function getPlatformRoles(): Promise<string[] | null> {
  const { config, store, client } = await getResources();
  const requestHeaders = await headers();
  const request = new Request(new URL('/', config.APP_BASE_URL), {
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

export function profileAccountUrl() {
  const origin = process.env.PROFILE_ORIGIN;
  if (!origin) throw new Error('Profile origin is unavailable');
  const url = new URL('/account', origin);
  if (
    url.protocol !== 'https:' ||
    url.username ||
    url.password ||
    url.search ||
    url.hash
  )
    throw new Error('Profile origin is invalid');
  return url.toString();
}
