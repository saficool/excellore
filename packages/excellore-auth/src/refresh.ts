import * as oidc from 'openid-client';
import type { ValkeySessionStore } from './session-store.js';
import { randomOpaqueId } from './session-store.js';

const REFRESH_MARGIN_SECONDS = 60;

export async function getAccessToken(
  sid: string,
  store: ValkeySessionStore,
  client: oidc.Configuration,
): Promise<string> {
  const initial = await store.get(sid);
  if (!initial) throw new Error('Session unavailable');
  if (
    initial.accessTokenExpiresAt >
    Math.floor(Date.now() / 1000) + REFRESH_MARGIN_SECONDS
  ) {
    return initial.accessToken;
  }

  const owner = randomOpaqueId();
  let locked = false;
  for (let attempt = 0; attempt < 300; attempt += 1) {
    if (await store.acquireRefreshLock(sid, owner)) {
      locked = true;
      break;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
    const current = await store.get(sid);
    if (!current) throw new Error('Session unavailable');
    if (
      current.accessTokenExpiresAt >
      Math.floor(Date.now() / 1000) + REFRESH_MARGIN_SECONDS
    ) {
      return current.accessToken;
    }
  }
  if (!locked) throw new Error('Session refresh unavailable');

  let leaseLost = false;
  const heartbeat = setInterval(() => {
    void store
      .extendRefreshLock(sid, owner)
      .then((held) => {
        if (!held) leaseLost = true;
      })
      .catch(() => {
        leaseLost = true;
      });
  }, 10_000);
  heartbeat.unref();
  try {
    const current = await store.get(sid);
    if (!current) throw new Error('Session unavailable');
    if (
      current.accessTokenExpiresAt >
      Math.floor(Date.now() / 1000) + REFRESH_MARGIN_SECONDS
    ) {
      return current.accessToken;
    }
    let tokens: Awaited<ReturnType<typeof oidc.refreshTokenGrant>>;
    try {
      tokens = await oidc.refreshTokenGrant(client, current.refreshToken);
    } catch {
      await store.delete(sid);
      throw new Error('Session refresh failed');
    }
    if (leaseLost || !(await store.extendRefreshLock(sid, owner))) {
      throw new Error('Session refresh unavailable');
    }
    if (!tokens.expires_in || tokens.expires_in <= 0) {
      await store.delete(sid);
      throw new Error('Session refresh failed');
    }
    const claims = tokens.claims();
    if (claims && claims.sub !== current.sub) {
      await store.delete(sid);
      throw new Error('Session refresh failed');
    }
    const updated = await store.update(sid, (record) => ({
      ...record,
      accessToken: tokens.access_token,
      accessTokenExpiresAt: Math.floor(Date.now() / 1000) + tokens.expires_in!,
      refreshToken: tokens.refresh_token ?? record.refreshToken,
      idToken: tokens.id_token ?? record.idToken,
      acr: typeof claims?.acr === 'string' ? claims.acr : record.acr,
      lastSeenAt: Math.floor(Date.now() / 1000),
    }));
    if (!updated) throw new Error('Session unavailable');
    return updated.accessToken;
  } finally {
    clearInterval(heartbeat);
    await store.releaseRefreshLock(sid, owner);
  }
}
