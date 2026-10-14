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
