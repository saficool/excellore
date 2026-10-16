import {
  canonicalBffRequest,
  getAccessToken,
  requireSession,
  SessionRequiredError,
} from '@excellore/auth';
import { getApiDependencies } from '../../../lib/server';

export const runtime = 'nodejs';
export const dynamic = 'force-dynamic';

export async function GET(request: Request): Promise<Response> {
  const dependencies = await getApiDependencies();
  const canonical = canonicalBffRequest(
    request,
    dependencies.config.APP_BASE_URL,
  );
  if (
    !canonical ||
    new URL(canonical.url).pathname !== '/api/avatar' ||
    new URL(canonical.url).search
  )
    return new Response(null, { status: 400 });
  try {
    const session = await requireSession(
      canonical,
      dependencies.config,
      dependencies.store,
    );
    const token = await getAccessToken(
      session.sid,
      dependencies.store,
      dependencies.oidcClient,
    );
    const apiOrigin = new URL(dependencies.apiBaseUrl);
    if (
      apiOrigin.pathname !== '/' ||
      apiOrigin.search ||
      apiOrigin.hash ||
      apiOrigin.username ||
      apiOrigin.password ||
      (apiOrigin.protocol !== 'https:' &&
        !(
          dependencies.config.EXCELLORE_INSECURE_LOCAL_DEV === 'true' &&
          apiOrigin.protocol === 'http:' &&
          ['localhost', '127.0.0.1'].includes(apiOrigin.hostname)
        ))
    )
      throw new Error('Invalid API origin');
    const response = await fetch(new URL('/v1/me/avatar', apiOrigin), {
      headers: { Authorization: `Bearer ${token}`, Accept: 'image/webp' },
      redirect: 'manual',
      cache: 'no-store',
      signal: AbortSignal.timeout(15_000),
    });
    if (response.status === 404)
      return new Response(null, {
        status: 404,
        headers: { 'Cache-Control': 'private, no-store' },
      });
    if (
      !response.ok ||
      response.headers.get('content-type') !== 'image/webp' ||
      !response.body
    )
      throw new Error('Avatar unavailable');
    return new Response(response.body, {
      status: 200,
      headers: {
        'Content-Type': 'image/webp',
        'Cache-Control': 'private, no-store',
        'X-Content-Type-Options': 'nosniff',
      },
    });
  } catch (error) {
    if (error instanceof SessionRequiredError)
      return new Response(null, {
        status: 401,
        headers: { 'Cache-Control': 'private, no-store' },
      });
    return new Response(null, {
      status: 503,
      headers: { 'Cache-Control': 'private, no-store' },
    });
  }
}
