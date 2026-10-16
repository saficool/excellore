import { canonicalBffRequest, proxyBffApi } from '@excellore/auth';
import { getTenantDependencies } from '../../../../../lib/server';

export const runtime = 'nodejs';
export const dynamic = 'force-dynamic';

async function handle(request: Request, slug: string, path: string[]) {
  const dependencies = await getTenantDependencies();
  const canonical = canonicalBffRequest(
    request,
    dependencies.config.APP_BASE_URL,
  );
  if (!canonical) return new Response(null, { status: 400 });
  return proxyBffApi({
    ...dependencies,
    scope: 'tenant',
    slug,
    segments: path,
    request: canonical,
    ...(path.length === 4 &&
    path[0] === 'audit-events' &&
    path[1] === 'exports' &&
    path[3] === 'download' &&
    typeof path[2] === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(path[2])
      ? {
          download: {
            contentType: 'application/x-ndjson' as const,
            maxBytes: 64 * 1024 * 1024,
          },
        }
      : {}),
  });
}

export async function GET(
  request: Request,
  { params }: { params: Promise<{ slug: string; path: string[] }> },
) {
  const p = await params;
  return handle(request, p.slug, p.path);
}
export async function POST(
  request: Request,
  { params }: { params: Promise<{ slug: string; path: string[] }> },
) {
  const p = await params;
  return handle(request, p.slug, p.path);
}
export async function PUT(
  request: Request,
  { params }: { params: Promise<{ slug: string; path: string[] }> },
) {
  const p = await params;
  return handle(request, p.slug, p.path);
}
export async function PATCH(
  request: Request,
  { params }: { params: Promise<{ slug: string; path: string[] }> },
) {
  const p = await params;
  return handle(request, p.slug, p.path);
}
export async function DELETE(
  request: Request,
  { params }: { params: Promise<{ slug: string; path: string[] }> },
) {
  const p = await params;
  return handle(request, p.slug, p.path);
}
