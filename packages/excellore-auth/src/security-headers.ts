import { randomBytes } from 'node:crypto';

export function createBffSecurityHeaders(
  requestHeaders: Headers,
  options: {
    development: boolean;
    secureOrigin: boolean;
  },
): { requestHeaders: Headers; responseHeaders: Headers } {
  const nonce = randomBytes(16).toString('base64');
  const policy = [
    "default-src 'none'",
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'${options.development ? " 'unsafe-eval'" : ''}`,
    "style-src 'self'",
    "img-src 'self' data:",
    "font-src 'self'",
    `connect-src 'self'${options.development ? ' ws://localhost:* ws://*.localhost:*' : ''}`,
    "form-action 'self'",
    "base-uri 'none'",
    "object-src 'none'",
    "frame-ancestors 'none'",
  ].join('; ');
  const forwarded = new Headers(requestHeaders);
  forwarded.set('x-nonce', nonce);
  forwarded.set('Content-Security-Policy', policy);
  const response = new Headers({
    'Content-Security-Policy': policy,
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'strict-origin-when-cross-origin',
    'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
  });
  if (options.secureOrigin)
    response.set(
      'Strict-Transport-Security',
      'max-age=31536000; includeSubDomains',
    );
  return { requestHeaders: forwarded, responseHeaders: response };
}
