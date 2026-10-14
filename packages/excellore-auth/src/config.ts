import { z } from 'zod';

const baseSchema = z.object({
  OIDC_ISSUER: z.url(),
  OIDC_CLIENT_ID: z.string().min(1),
  OIDC_CLIENT_SECRET: z.string().min(1),
  OIDC_SCOPES: z
    .string()
    .refine((value) => value.split(/\s+/).includes('openid')),
  APP_BASE_URL: z.url(),
  SESSION_COOKIE_NAME: z
    .string()
    .regex(/^(?:__Host-)?excellore-[a-z][a-z0-9-]*$/),
  SESSION_STORE_URL: z.url(),
  SESSION_KEY_PREFIX: z.string().regex(/^[a-z][a-z0-9-]*:sess:$/),
  SESSION_ENCRYPTION_KEY: z
    .string()
    .regex(/^[A-Za-z0-9_-]{43}$/)
    .refine((value) => Buffer.from(value, 'base64url').length === 32),
  EXCELLORE_INSECURE_LOCAL_DEV: z.enum(['true']).optional(),
  NODE_ENV: z.enum(['development', 'test', 'production']).optional(),
});

export const authConfigSchema = baseSchema.superRefine((value, context) => {
  const local =
    value.EXCELLORE_INSECURE_LOCAL_DEV === 'true' &&
    (value.NODE_ENV === 'development' || value.NODE_ENV === 'test');
  if (value.EXCELLORE_INSECURE_LOCAL_DEV === 'true' && !local) {
    context.addIssue({
      code: 'custom',
      path: ['EXCELLORE_INSECURE_LOCAL_DEV'],
      message: 'Insecure local mode requires development or test',
    });
  }
  for (const field of ['OIDC_ISSUER', 'APP_BASE_URL'] as const) {
    const url = new URL(value[field]);
    if (
      url.username ||
      url.password ||
      url.search ||
      url.hash ||
      (url.protocol !== 'https:' &&
        !(
          local &&
          url.protocol === 'http:' &&
          (url.hostname === 'localhost' || url.hostname.endsWith('.localhost'))
        ))
    ) {
      context.addIssue({
        code: 'custom',
        path: [field],
        message: 'A secure URL is required',
      });
    }
  }
  if (new URL(value.APP_BASE_URL).pathname !== '/') {
    context.addIssue({
      code: 'custom',
      path: ['APP_BASE_URL'],
      message: 'Use an origin without a path',
    });
  }
  if (!local && !value.SESSION_COOKIE_NAME.startsWith('__Host-')) {
    context.addIssue({
      code: 'custom',
      path: ['SESSION_COOKIE_NAME'],
      message: 'A __Host- cookie is required',
    });
  }
  if (
    local &&
    value.SESSION_COOKIE_NAME.startsWith('__Host-') &&
    new URL(value.APP_BASE_URL).protocol === 'http:'
  ) {
    context.addIssue({
      code: 'custom',
      path: ['SESSION_COOKIE_NAME'],
      message: 'Use a dev-only cookie name over local HTTP',
    });
  }
  if (
    !['redis:', 'rediss:'].includes(new URL(value.SESSION_STORE_URL).protocol)
  ) {
    context.addIssue({
      code: 'custom',
      path: ['SESSION_STORE_URL'],
      message: 'A Valkey URL is required',
    });
  }
});

export type AuthConfig = z.infer<typeof authConfigSchema>;

export function loadAuthConfig(
  env: Record<string, string | undefined> = process.env,
): AuthConfig {
  return authConfigSchema.parse(env);
}

export const accessClientConfigSchema = z
  .object({
    ACCESS_INTERNAL_URL: z.url(),
    AUTHZ_CLIENT_ID: z.string().min(1),
    AUTHZ_CLIENT_SECRET: z.string().min(1),
  })
  .superRefine((value, context) => {
    const url = new URL(value.ACCESS_INTERNAL_URL);
    if (
      !['http:', 'https:'].includes(url.protocol) ||
      url.username ||
      url.password ||
      url.pathname !== '/' ||
      url.search ||
      url.hash
    ) {
      context.addIssue({
        code: 'custom',
        path: ['ACCESS_INTERNAL_URL'],
        message: 'Use an HTTP(S) origin without credentials, path, or query',
      });
    }
  });

export type AccessClientConfig = z.infer<typeof accessClientConfigSchema>;

export function loadAccessClientConfig(
  env: Record<string, string | undefined> = process.env,
): AccessClientConfig {
  return accessClientConfigSchema.parse(env);
}
