import { canonicalBffRequest, proxyBffApi } from '@excellore/auth';
import { getApiDependencies } from '../../../../lib/server';

export const runtime = 'nodejs';
export const dynamic = 'force-dynamic';

async function handle(request: Request, path: string[]) {
  const dependencies = await getApiDependencies();
  const canonical = canonicalBffRequest(
    request,
    dependencies.config.APP_BASE_URL,
  );
  if (!canonical) return new Response(null, { status: 400 });
  return proxyBffApi({
    ...dependencies,
    scope: 'platform',
    segments: path,
    request: canonical,
  });
}

type RouteContext = { params: Promise<{ path: string[] }> };
export async function GET(request: Request, { params }: RouteContext) {
  return handle(request, (await params).path);
}
export async function POST(request: Request, { params }: RouteContext) {
  return handle(request, (await params).path);
}
export async function PUT(request: Request, { params }: RouteContext) {
  return handle(request, (await params).path);
}
export async function PATCH(request: Request, { params }: RouteContext) {
  return handle(request, (await params).path);
}
