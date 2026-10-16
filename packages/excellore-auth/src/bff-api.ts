import type * as oidc from 'openid-client';
import type { AccessInternalClient } from './access-client.js';
import type { AuthConfig } from './config.js';
import { getAccessToken } from './refresh.js';
import type { ValkeySessionStore } from './session-store.js';
import {
  requireSession,
  requireTenant,
  SessionRequiredError,
  TenantAccessError,
} from './tenant.js';

const safeSegment = /^[a-z0-9][a-z0-9-]{0,127}$/;
const actionSegments = new Set([
  'members:lookup',
  'members:suspend',
  'members:reactivate',
  'owner-invitation:resend',
]);
const writeMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function problem(status: number, name: string): Response {
  return Response.json(
    { type: `urn:excellore:problem:${name}`, status },
    {
      status,
      headers: {
        'Content-Type': 'application/problem+json',
        'Cache-Control': 'no-store',
      },
    },
  );
}

export function validateApiProxyRequest(
  request: Request,
  appOrigin: string,
  segments: readonly string[],
): number | null {
  const url = new URL(request.url);
  if (
    url.origin !== new URL(appOrigin).origin ||
    url.search ||
    url.hash ||
    !['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE'].includes(
      request.method,
    ) ||
    segments.length > 12 ||
    segments.some(
      (segment) => !safeSegment.test(segment) && !actionSegments.has(segment),
    )
  )
    return 400;
  if (
    writeMethods.has(request.method) &&
    (request.headers.get('origin') !== url.origin ||
      request.headers.get('x-excellore-csrf') !== '1')
  )
    return 403;
  return null;
}

export interface BffApiOptions {
  config: AuthConfig;
  store: ValkeySessionStore;
  oidcClient: oidc.Configuration;
  access?: Pick<AccessInternalClient, 'resolveSlug' | 'resolve'>;
  app: string;
  apiBaseUrl: string;
  scope: 'me' | 'tenant' | 'platform';
  slug?: string;
  segments: readonly string[];
  request: Request;
  fetchImpl?: typeof fetch;
  download?: { contentType: 'application/x-ndjson'; maxBytes: number };
}

export async function proxyBffApi(options: BffApiOptions): Promise<Response> {
  const {
    config,
    store,
    oidcClient,
    access,
    app,
    apiBaseUrl,
    scope,
    slug,
    segments,
    request,
  } = options;
  const invalid = validateApiProxyRequest(
    request,
    config.APP_BASE_URL,
    segments,
  );
  if (invalid)
    return problem(invalid, invalid === 403 ? 'forbidden' : 'validation');
  let apiOrigin: URL;
  try {
    apiOrigin = new URL(apiBaseUrl);
    const local =
      config.EXCELLORE_INSECURE_LOCAL_DEV === 'true' &&
      (apiOrigin.hostname === 'localhost' ||
        apiOrigin.hostname.endsWith('.localhost'));
    if (
      (apiOrigin.protocol !== 'https:' &&
        !(local && apiOrigin.protocol === 'http:')) ||
      apiOrigin.username ||
      apiOrigin.password ||
      apiOrigin.pathname !== '/' ||
      apiOrigin.search ||
      apiOrigin.hash
    )
      throw new Error();
  } catch {
    return problem(503, 'authz-unavailable');
  }
  try {
    const session = await requireSession(request, config, store);
    let path: string;
    if (scope === 'tenant') {
      if (!slug || !access) return problem(503, 'authz-unavailable');
      const tenant = await requireTenant(
        request,
        slug,
        config,
        store,
        access,
        app,
      );
      path = `/v1/tenants/${tenant.tenantId}/${segments.join('/')}`;
    } else if (scope === 'platform') {
      path = `/v1/platform/${segments.join('/')}`;
    } else {
      path = `/v1/me/${segments.join('/')}`;
    }
    const token = await getAccessToken(session.sid, store, oidcClient);
    const body = writeMethods.has(request.method)
      ? await request.arrayBuffer()
      : undefined;
    if (body && body.byteLength > 1_048_576) return problem(413, 'validation');
    const response = await (options.fetchImpl ?? fetch)(
      new URL(path, apiOrigin),
      {
        method: request.method,
        headers: {
          Authorization: `Bearer ${token}`,
          Accept: options.download
            ? 'application/x-ndjson, application/problem+json'
            : 'application/json, application/problem+json',
          ...(body && request.headers.get('content-type')
            ? { 'Content-Type': request.headers.get('content-type')! }
            : {}),
        },
        ...(body ? { body } : {}),
        redirect: 'manual',
        cache: 'no-store',
        signal: AbortSignal.timeout(options.download ? 120_000 : 15_000),
      },
    );
    if (response.status >= 300 && response.status < 400)
      return problem(503, 'authz-unavailable');
    const contentType = response.headers.get('content-type')?.split(';')[0];
    const json =
      contentType === 'application/json' ||
      contentType === 'application/problem+json';
    if (
      options.download &&
      response.ok &&
      contentType === options.download.contentType &&
      request.method === 'GET' &&
      options.download.maxBytes > 0 &&
      response.body
    ) {
      const length = Number(response.headers.get('content-length'));
      if (length > options.download.maxBytes)
        return problem(503, 'authz-unavailable');
      let bytes = 0;
      const limiter = new TransformStream<Uint8Array, Uint8Array>({
        transform(chunk, controller) {
          bytes += chunk.byteLength;
          if (bytes > options.download!.maxBytes) {
            controller.error(new Error('Download size exceeded'));
            return;
          }
          controller.enqueue(chunk);
        },
      });
      return new Response(response.body.pipeThrough(limiter), {
        status: response.status,
        headers: {
          'Cache-Control': 'no-store',
          'Content-Type': options.download.contentType,
          'Content-Disposition': 'attachment; filename="audit-export.jsonl"',
          'X-Content-Type-Options': 'nosniff',
        },
      });
    }
    if (response.body && !json) {
      const reader = response.body.getReader();
      while (true) {
        const chunk = await reader.read();
        if (chunk.value?.byteLength) {
          await reader.cancel();
          return problem(503, 'authz-unavailable');
        }
        if (chunk.done) break;
      }
    }
    return new Response(json ? response.body : null, {
      status: response.status,
      headers: {
        'Cache-Control': 'no-store',
        ...(json ? { 'Content-Type': contentType! } : {}),
      },
    });
  } catch (error) {
    if (error instanceof SessionRequiredError)
      return problem(401, 'authentication-required');
    if (error instanceof TenantAccessError)
      return problem(
        error.status,
        error.status === 404 ? 'not-found' : 'forbidden',
      );
    return problem(503, 'authz-unavailable');
  }
}
