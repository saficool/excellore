import { NextRequest, NextResponse } from 'next/server';
import { createBffSecurityHeaders } from '@excellore/auth';

export function proxy(request: NextRequest) {
  const { requestHeaders, responseHeaders } = createBffSecurityHeaders(
    request.headers,
    {
      development: process.env.NODE_ENV === 'development',
      secureOrigin: process.env.APP_BASE_URL?.startsWith('https://') ?? false,
    },
  );
  const response = NextResponse.next({ request: { headers: requestHeaders } });
  responseHeaders.forEach((value, name) => response.headers.set(name, value));
  return response;
}

export const config = {
  matcher: ['/((?!_next/static|_next/image|favicon.ico).*)'],
};
