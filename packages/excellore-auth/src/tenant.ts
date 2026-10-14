import type {
  AccessInternalClient,
  AuthzSnapshot,
  ResolvedTenant,
} from './access-client.js';
import type { AuthConfig } from './config.js';
import { readOpaqueCookie } from './cookies.js';
import type { SessionRecord, ValkeySessionStore } from './session-store.js';

export class SessionRequiredError extends Error {
  readonly status = 401;
  readonly redirectTo: string;

  constructor(path: string) {
    super('Session required');
    this.redirectTo = `/auth/login?returnTo=${encodeURIComponent(path)}`;
  }
}

export class TenantAccessError extends Error {
  constructor(readonly status: 403 | 404) {
    super(status === 404 ? 'Tenant not found' : 'App access denied');
  }
}

export async function requireSession(
  request: Request,
  config: AuthConfig,
  store: Pick<ValkeySessionStore, 'get'>,
): Promise<SessionRecord> {
  const sid = readOpaqueCookie(request, config.SESSION_COOKIE_NAME);
  const session = sid ? await store.get(sid) : null;
  if (session) return session;
  const path = new URL(request.url).pathname;
  throw new SessionRequiredError(
    path.startsWith('/') && !path.startsWith('//') ? path : '/',
  );
}

export type TenantContext = ResolvedTenant & AuthzSnapshot;

export async function requireTenant(
  request: Request,
  slug: string,
  config: AuthConfig,
  store: Pick<ValkeySessionStore, 'get'>,
  access: Pick<AccessInternalClient, 'resolveSlug' | 'resolve'>,
  app: string,
): Promise<TenantContext> {
  const session = await requireSession(request, config, store);
  if (!/^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$/.test(slug))
    throw new TenantAccessError(404);
  const tenant = await access.resolveSlug(session.sub, slug);
  if (!tenant) throw new TenantAccessError(404);
  const snapshot = await access.resolve(session.sub, tenant.tenantId);
  if (snapshot.decision !== 'allow') throw new TenantAccessError(404);
  if (!/^[a-z][a-z0-9-]*$/.test(app)) throw new TenantAccessError(403);
  if (
    app !== 'shell' &&
    (!snapshot.apps.includes(app) ||
      !snapshot.permissions.includes(`${app}.app.access`))
  )
    throw new TenantAccessError(403);
  return { ...tenant, ...snapshot };
}
