'use client';

export class PlatformApiError extends Error {
  constructor(
    readonly status: number,
    readonly type: string,
  ) {
    super('The request could not be completed.');
  }
}

export async function apiRequest<T>(
  path: string,
  method = 'GET',
  body?: object,
): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    cache: 'no-store',
    headers: {
      Accept: 'application/json, application/problem+json',
      ...(method !== 'GET' ? { 'X-Excellore-CSRF': '1' } : {}),
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new PlatformApiError(
      response.status,
      typeof problem?.type === 'string' ? problem.type : '',
    );
  }
  return response.status === 204 ||
    !response.headers.get('content-type')?.includes('json')
    ? (undefined as T)
    : ((await response.json()) as T);
}

export function errorMessage(error: unknown) {
  if (error instanceof PlatformApiError) {
    if (error.type === 'urn:excellore:problem:step-up-required')
      return 'Confirm your identity with MFA, then retry this action.';
    if (error.status === 401) return 'Your session expired. Sign in again.';
    if (error.status === 403)
      return 'You do not have permission for this action.';
    if (error.status === 404) return 'This item is unavailable.';
    if (error.status === 409)
      return 'This action conflicts with the current state.';
    if (error.status === 503) return 'The service is temporarily unavailable.';
  }
  return 'The request could not be completed.';
}
