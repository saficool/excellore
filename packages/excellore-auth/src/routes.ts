import * as oidc from 'openid-client';
import { createRemoteJWKSet, jwtVerify } from 'jose';
import type { AuthConfig } from './config.js';
import { readOpaqueCookie } from './cookies.js';
import { randomOpaqueId, type ValkeySessionStore } from './session-store.js';

const LOGOUT_EVENT = 'http://schemas.openid.net/event/backchannel-logout';

function setCookie(
  name: string,
  value: string,
  secure: boolean,
  maxAge: number,
): string {
  return `${name}=${value}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${maxAge}${secure ? '; Secure' : ''}`;
}

function boundedClaim(value: unknown, maximum: number): string | undefined {
  if (typeof value !== 'string') return undefined;
  const normalized = value.trim();
  return normalized.length > 0 && normalized.length <= maximum
    ? normalized
    : undefined;
}

function noStore(headers: Headers): void {
  headers.set('Cache-Control', 'no-store');
  headers.set('Referrer-Policy', 'no-referrer');
  headers.set('X-Content-Type-Options', 'nosniff');
}

function badRequest(): Response {
  const headers = new Headers();
  noStore(headers);
  return new Response('Invalid authentication request', {
    status: 400,
    headers,
  });
}

async function limitedBody(
  request: Request,
  maxBytes: number,
): Promise<string | null> {
  const reader = request.body?.getReader();
  if (!reader) return null;
  const chunks: Uint8Array[] = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > maxBytes) {
      await reader.cancel();
      return null;
    }
    chunks.push(value);
  }
  return new TextDecoder().decode(Buffer.concat(chunks));
}

export function validateReturnTo(value: string | null): string | null {
  if (value === null) return '/';
  if (
    value.length < 1 ||
    value.length > 2048 ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    value.includes('\\') ||
    value.includes('?') ||
    value.includes('#') ||
    [...value].some(
      (character) =>
        character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127,
    )
  )
    return null;
  return value;
}

export function createAuthHandlers(
  config: AuthConfig,
  store: ValkeySessionStore,
  client: oidc.Configuration,
) {
  const appOrigin = new URL(config.APP_BASE_URL).origin;
  const secureCookie = new URL(config.APP_BASE_URL).protocol === 'https:';
  const flowCookie = `${config.SESSION_COOKIE_NAME}-flow`;
  const jwksUri = client.serverMetadata().jwks_uri;
  if (!jwksUri) throw new Error('OIDC issuer has no JWKS URI');
  const jwks = createRemoteJWKSet(new URL(jwksUri));
  const endSessionEndpoint = client.serverMetadata().end_session_endpoint;
  if (
    !endSessionEndpoint ||
    new URL(endSessionEndpoint).origin !== new URL(config.OIDC_ISSUER).origin
  ) {
    throw new Error('OIDC issuer has no trusted logout endpoint');
  }

  function correctOrigin(request: Request): boolean {
    return new URL(request.url).origin === appOrigin;
  }

  async function beginAuthorization(
    returnTo: string,
    expectedSub?: string,
    extra: Record<string, string> = {},
  ): Promise<Response> {
    const flowId = randomOpaqueId();
    const state = oidc.randomState();
    const nonce = oidc.randomNonce();
    const verifier = oidc.randomPKCECodeVerifier();
    const challenge = await oidc.calculatePKCECodeChallenge(verifier);
    await store.createFlow(flowId, {
      state,
      nonce,
      verifier,
      returnTo,
      ...(expectedSub ? { expectedSub } : {}),
      ...(extra.acr_values ? { minimumAcr: extra.acr_values } : {}),
    });
    const redirect = oidc.buildAuthorizationUrl(client, {
      response_type: 'code',
      client_id: config.OIDC_CLIENT_ID,
      redirect_uri: new URL('/auth/callback', appOrigin).href,
      scope: config.OIDC_SCOPES,
      state,
      nonce,
      code_challenge: challenge,
      code_challenge_method: 'S256',
      ...extra,
    });
    const headers = new Headers({ Location: redirect.href });
    noStore(headers);
    headers.append(
      'Set-Cookie',
      setCookie(flowCookie, flowId, secureCookie, 300),
    );
    return new Response(null, { status: 302, headers });
  }

  return {
    async login(request: Request): Promise<Response> {
      if (
        request.method !== 'GET' ||
        !correctOrigin(request) ||
        new URL(request.url).pathname !== '/auth/login'
      )
        return badRequest();
      const returnTo = validateReturnTo(
        new URL(request.url).searchParams.get('returnTo'),
      );
      if (!returnTo) return badRequest();
      return beginAuthorization(returnTo);
    },

    async stepUp(request: Request): Promise<Response> {
      if (
        request.method !== 'GET' ||
        !correctOrigin(request) ||
        new URL(request.url).pathname !== '/auth/step-up'
      )
        return badRequest();
      const returnTo = validateReturnTo(
        new URL(request.url).searchParams.get('returnTo'),
      );
      if (!returnTo) return badRequest();
      const sid = readOpaqueCookie(request, config.SESSION_COOKIE_NAME);
      const session = sid ? await store.get(sid) : null;
      if (!session) return badRequest();
      return beginAuthorization(returnTo, session.sub, {
        acr_values: '2',
        prompt: 'login',
        max_age: '0',
      });
    },

    async accountAction(request: Request): Promise<Response> {
      if (
        request.method !== 'GET' ||
        !correctOrigin(request) ||
        new URL(request.url).pathname !== '/auth/account-action'
      )
        return badRequest();
      const url = new URL(request.url);
      const returnTo = validateReturnTo(url.searchParams.get('returnTo'));
      const action = url.searchParams.get('action');
      if (
        !returnTo ||
        !action ||
        !['UPDATE_PASSWORD', 'UPDATE_EMAIL', 'CONFIGURE_TOTP'].includes(action)
      )
        return badRequest();
      const sid = readOpaqueCookie(request, config.SESSION_COOKIE_NAME);
      const session = sid ? await store.get(sid) : null;
      if (!session) return badRequest();
      return beginAuthorization(returnTo, session.sub, { kc_action: action });
    },

    async callback(request: Request): Promise<Response> {
      if (
        request.method !== 'GET' ||
        !correctOrigin(request) ||
        new URL(request.url).pathname !== '/auth/callback'
      )
        return badRequest();
      const flowId = readOpaqueCookie(request, flowCookie);
      if (!flowId) return badRequest();
      const flow = await store.consumeFlow(flowId);
      if (
        !flow ||
        new URL(request.url).searchParams.get('state') !== flow.state
      )
        return badRequest();
      try {
        const tokens = await oidc.authorizationCodeGrant(
          client,
          new URL(request.url),
          {
            pkceCodeVerifier: flow.verifier,
            expectedState: flow.state,
            expectedNonce: flow.nonce,
            idTokenExpected: true,
          },
        );
        const claims = tokens.claims();
        if (
          !claims ||
          typeof claims.sub !== 'string' ||
          typeof claims.sid !== 'string' ||
          !tokens.id_token ||
          !tokens.refresh_token ||
          !tokens.expires_in ||
          tokens.expires_in <= 0
        ) {
          return badRequest();
        }
        if (flow.expectedSub && claims.sub !== flow.expectedSub)
          return badRequest();
        if (
          flow.minimumAcr &&
          (typeof claims.acr !== 'string' ||
            !/^\d+$/.test(claims.acr) ||
            Number(claims.acr) < Number(flow.minimumAcr))
        )
          return badRequest();
        const session = await store.create({
          kcSid: claims.sid,
          sub: claims.sub,
          acr: typeof claims.acr === 'string' ? claims.acr : '',
          displayName:
            boundedClaim(claims.name, 200) ??
            boundedClaim(claims.preferred_username, 200),
          email: boundedClaim(claims.email, 320),
          accessToken: tokens.access_token,
          accessTokenExpiresAt:
            Math.floor(Date.now() / 1000) + tokens.expires_in,
          refreshToken: tokens.refresh_token,
          idToken: tokens.id_token,
        });
        const old = readOpaqueCookie(request, config.SESSION_COOKIE_NAME);
        if (old) await store.delete(old);
        const headers = new Headers({
          Location: new URL(flow.returnTo, appOrigin).href,
        });
        noStore(headers);
        headers.append(
          'Set-Cookie',
          setCookie(flowCookie, '', secureCookie, 0),
        );
        headers.append(
          'Set-Cookie',
          setCookie(
            config.SESSION_COOKIE_NAME,
            session.sid,
            secureCookie,
            9 * 60 * 60,
          ),
        );
        return new Response(null, { status: 302, headers });
      } catch {
        return badRequest();
      }
    },

    async session(request: Request): Promise<Response> {
      if (
        request.method !== 'GET' ||
        !correctOrigin(request) ||
        new URL(request.url).pathname !== '/auth/session'
      )
        return badRequest();
      const sid = readOpaqueCookie(request, config.SESSION_COOKIE_NAME);
      const session = sid ? await store.get(sid) : null;
      const headers = new Headers({ 'Content-Type': 'application/json' });
      noStore(headers);
      return new Response(
        JSON.stringify(
          session
            ? { authenticated: true, acr: session.acr }
            : { authenticated: false },
        ),
        { status: 200, headers },
      );
    },

    async logout(request: Request): Promise<Response> {
      if (
        request.method !== 'POST' ||
        !correctOrigin(request) ||
        new URL(request.url).pathname !== '/auth/logout' ||
        request.headers.get('origin') !== appOrigin
      )
        return badRequest();
      const sid = readOpaqueCookie(request, config.SESSION_COOKIE_NAME);
      const session = sid ? await store.get(sid) : null;
      let remoteSucceeded = true;
      if (session) {
        try {
          const response = await fetch(endSessionEndpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams({
              id_token_hint: session.idToken,
              client_id: config.OIDC_CLIENT_ID,
            }),
            redirect: 'manual',
            cache: 'no-store',
            signal: AbortSignal.timeout(10_000),
          });
          remoteSucceeded = response.ok;
        } catch {
          remoteSucceeded = false;
        }
        await store.revokeSubjectGlobally(session.sub);
        await store.delete(session.sid);
      }
      const headers = new Headers({ Location: new URL('/', appOrigin).href });
      noStore(headers);
      headers.append(
        'Set-Cookie',
        setCookie(config.SESSION_COOKIE_NAME, '', secureCookie, 0),
      );
      return new Response(null, {
        status: remoteSucceeded ? 303 : 503,
        headers,
      });
    },

    async backchannelLogout(request: Request): Promise<Response> {
      if (
        request.method !== 'POST' ||
        !correctOrigin(request) ||
        new URL(request.url).pathname !== '/auth/backchannel-logout' ||
        request.headers
          .get('content-type')
          ?.split(';')[0]
          ?.trim()
          .toLowerCase() !== 'application/x-www-form-urlencoded'
      ) {
        return badRequest();
      }
      const body = await limitedBody(request, 16_384);
      if (body === null) return badRequest();
      const params = new URLSearchParams(body);
      const token = params.get('logout_token');
      if (!token || params.getAll('logout_token').length !== 1)
        return badRequest();
      let payload;
      try {
        ({ payload } = await jwtVerify(token, jwks, {
          issuer: config.OIDC_ISSUER,
          audience: config.OIDC_CLIENT_ID,
          algorithms: ['RS256'],
          maxTokenAge: '5 minutes',
          requiredClaims: ['iat', 'exp', 'jti', 'events'],
        }));
      } catch {
        return badRequest();
      }
      const event = payload.events;
      if (
        'nonce' in payload ||
        typeof payload.jti !== 'string' ||
        (typeof payload.sid !== 'string' && typeof payload.sub !== 'string') ||
        (payload.sid !== undefined && typeof payload.sid !== 'string') ||
        (payload.sub !== undefined && typeof payload.sub !== 'string') ||
        typeof event !== 'object' ||
        event === null ||
        Array.isArray(event) ||
        Object.keys(event).length !== 1 ||
        JSON.stringify((event as Record<string, unknown>)[LOGOUT_EVENT]) !==
          '{}'
      )
        return badRequest();
      try {
        if (typeof payload.sid === 'string')
          await store.deleteByKeycloakSession(payload.sid);
        else if (typeof payload.sub === 'string')
          await store.deleteBySubject(payload.sub);
        await store.claimLogoutToken(payload.jti);
        return new Response(null, { status: 200 });
      } catch {
        return new Response(null, { status: 503 });
      }
    },
  };
}
