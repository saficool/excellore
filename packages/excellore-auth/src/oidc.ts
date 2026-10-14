import * as oidc from 'openid-client';
import type { AuthConfig } from './config.js';

export async function createOidcClient(
  config: AuthConfig,
): Promise<oidc.Configuration> {
  const options: oidc.DiscoveryRequestOptions = {
    execute: [oidc.enableNonRepudiationChecks],
  };
  if (config.EXCELLORE_INSECURE_LOCAL_DEV === 'true') {
    options.execute?.push(oidc.allowInsecureRequests);
  }
  return oidc.discovery(
    new URL(config.OIDC_ISSUER),
    config.OIDC_CLIENT_ID,
    config.OIDC_CLIENT_SECRET,
    undefined,
    options,
  );
}
