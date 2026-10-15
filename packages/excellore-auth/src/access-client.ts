import * as oidc from 'openid-client';
import { z } from 'zod';
import type { AccessClientConfig, AuthConfig } from './config.js';

const id = z.uuid();
const slug = z.string().regex(/^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$/);
const code = z.string().min(1).max(128);
const tenantSchema = z.object({
  tenantId: id,
  slug,
  name: z.string().min(1),
});
const tenantListSchema = z.array(
  tenantSchema.extend({
    status: z.enum([
      'provisioning',
      'active',
      'suspended',
      'pending_deletion',
      'deleted',
    ]),
    roles: z.array(code),
    apps: z.array(code),
  }),
);
const invitationListSchema = z.array(
  z.object({
    id,
    tenantId: id,
    tenantName: z.string().min(1),
    expiresAt: z.iso.datetime({ offset: true }),
  }),
);
const snapshotSchema = z.object({
  tenantId: id,
  decision: z.enum(['allow', 'deny']).optional(),
  tenantStatus: z.string().optional(),
  membershipStatus: z.string().optional(),
  roles: z.array(code).optional(),
  permissions: z.array(code),
  apps: z.array(code).optional(),
  version: z.number().int().nonnegative().optional(),
  resolvedAt: z.iso.datetime().optional(),
});

export type ResolvedTenant = z.infer<typeof tenantSchema>;
export type ListedTenant = z.infer<typeof tenantListSchema>[number];
export type PendingInvitation = z.infer<typeof invitationListSchema>[number];
export type AuthzSnapshot = {
  tenantId: string;
  decision: 'allow' | 'deny';
  permissions: readonly string[];
  apps: readonly string[];
};

export class AccessUnavailableError extends Error {
  readonly status = 503;
  constructor() {
    super('Access authority unavailable');
  }
}

export class AccessInternalClient {
  private cachedToken: { value: string; refreshAt: number } | null = null;
  private tokenPromise: Promise<string> | null = null;

  constructor(
    private readonly config: AccessClientConfig,
    private readonly machine: oidc.Configuration,
    private readonly request: typeof fetch = fetch,
  ) {}

  private async machineToken(): Promise<string> {
    if (this.cachedToken && Date.now() < this.cachedToken.refreshAt)
      return this.cachedToken.value;
    if (!this.tokenPromise) {
      this.tokenPromise = (async () => {
        const response = await oidc.clientCredentialsGrant(this.machine);
        if (
          !response.access_token ||
          response.token_type.toLowerCase() !== 'bearer' ||
          !response.expires_in ||
          response.expires_in <= 0
        )
          throw new AccessUnavailableError();
        this.cachedToken = {
          value: response.access_token,
          refreshAt: Date.now() + Math.max(0, response.expires_in - 60) * 1000,
        };
        return response.access_token;
      })().finally(() => {
        this.tokenPromise = null;
      });
    }
    try {
      return await this.tokenPromise;
    } catch {
      throw new AccessUnavailableError();
    }
  }

  private async post(path: string, body: object): Promise<unknown | null> {
    let response: Response;
    try {
      response = await this.request(
        new URL(path, this.config.ACCESS_INTERNAL_URL),
        {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${await this.machineToken()}`,
            'Content-Type': 'application/json',
            Accept: 'application/json',
          },
          body: JSON.stringify(body),
          cache: 'no-store',
          redirect: 'manual',
          signal: AbortSignal.timeout(5_000),
        },
      );
      if (response.status === 404) return null;
      if (response.status !== 200) throw new AccessUnavailableError();
      if (Number(response.headers.get('content-length')) > 65_536)
        throw new AccessUnavailableError();
      const text = await response.text();
      if (Buffer.byteLength(text) > 65_536) throw new AccessUnavailableError();
      return JSON.parse(text) as unknown;
    } catch {
      throw new AccessUnavailableError();
    }
  }

  async resolveSlug(
    userId: string,
    requestedSlug: string,
  ): Promise<ResolvedTenant | null> {
    id.parse(userId);
    slug.parse(requestedSlug);
    const response = await this.post('/internal/v1/tenants:resolve-slug', {
      userId,
      slug: requestedSlug,
    });
    if (response === null) return null;
    const tenant = tenantSchema.safeParse(response);
    if (!tenant.success || tenant.data.slug !== requestedSlug)
      throw new AccessUnavailableError();
    return tenant.data;
  }

  async listTenants(userId: string): Promise<ListedTenant[]> {
    id.parse(userId);
    const response = await this.post('/internal/v1/users/tenants:list', {
      userId,
    });
    const tenants = tenantListSchema.safeParse(response);
    if (!tenants.success) throw new AccessUnavailableError();
    return tenants.data;
  }

  async listPendingInvitations(userId: string): Promise<PendingInvitation[]> {
    id.parse(userId);
    const response = await this.post('/internal/v1/users/invitations:list', {
      userId,
    });
    const invitations = invitationListSchema.safeParse(response);
    if (!invitations.success) throw new AccessUnavailableError();
    return invitations.data;
  }

  async resolve(userId: string, tenantId: string): Promise<AuthzSnapshot> {
    id.parse(userId);
    id.parse(tenantId);
    const response = await this.post('/internal/v1/authz/resolve', {
      userId,
      tenantId,
    });
    const snapshot = snapshotSchema.safeParse(response);
    if (!snapshot.success || snapshot.data.tenantId !== tenantId)
      throw new AccessUnavailableError();
    const value = snapshot.data;
    if (value.decision === 'deny' && value.permissions.length === 0) {
      return { tenantId, decision: 'deny', permissions: [], apps: [] };
    }
    if (
      value.decision === 'deny' ||
      value.tenantStatus !== 'active' ||
      value.membershipStatus !== 'active' ||
      !value.apps ||
      !value.roles ||
      value.version === undefined ||
      value.resolvedAt === undefined
    )
      throw new AccessUnavailableError();
    return {
      tenantId,
      decision: 'allow',
      permissions: value.permissions,
      apps: value.apps,
    };
  }
}

export async function createAccessClient(
  auth: AuthConfig,
  access: AccessClientConfig,
  request: typeof fetch = fetch,
): Promise<AccessInternalClient> {
  const options: oidc.DiscoveryRequestOptions = {};
  if (auth.EXCELLORE_INSECURE_LOCAL_DEV === 'true')
    options.execute = [oidc.allowInsecureRequests];
  const machine = await oidc.discovery(
    new URL(auth.OIDC_ISSUER),
    access.AUTHZ_CLIENT_ID,
    access.AUTHZ_CLIENT_SECRET,
    undefined,
    options,
  );
  return new AccessInternalClient(access, machine, request);
}
