import { canonicalRequest, getAuthHandlers } from '../../../lib/server';

export const runtime = 'nodejs';
export const dynamic = 'force-dynamic';

const actions = [
  'login',
  'callback',
  'logout',
  'backchannel-logout',
  'session',
  'step-up',
  'account-action',
] as const;
type Action = (typeof actions)[number];

async function dispatch(request: Request, action: string): Promise<Response> {
  if (!actions.includes(action as Action))
    return new Response(null, { status: 404 });
  const canonical = await canonicalRequest(request);
  if (!canonical) return new Response(null, { status: 400 });
  const handlers = await getAuthHandlers();
  switch (action) {
    case 'login':
      return handlers.login(canonical);
    case 'callback':
      return handlers.callback(canonical);
    case 'logout':
      return handlers.logout(canonical);
    case 'backchannel-logout':
      return handlers.backchannelLogout(canonical);
    case 'session':
      return handlers.session(canonical);
    case 'step-up':
      return handlers.stepUp(canonical);
    case 'account-action':
      return handlers.accountAction(canonical);
    default:
      return new Response(null, { status: 404 });
  }
}

export async function GET(
  request: Request,
  { params }: { params: Promise<{ action: string }> },
) {
  return dispatch(request, (await params).action);
}
export async function POST(
  request: Request,
  { params }: { params: Promise<{ action: string }> },
) {
  return dispatch(request, (await params).action);
}
