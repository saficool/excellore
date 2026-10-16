'use client';

import { useCallback, useEffect, useState } from 'react';

export class AccessApiError extends Error {
  constructor(
    readonly status: number,
    readonly type: string,
  ) {
    super('The request could not be completed.');
  }
}

export function tenantApiPath(slug: string, path: string) {
  if (!/^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$/.test(slug))
    throw new Error('Invalid tenant');
  if (!/^[a-z0-9:/-]+$/.test(path) || path.startsWith('/'))
    throw new Error('Invalid API path');
  return `/api/t/${slug}/${path}`;
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
    throw new AccessApiError(
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
  if (error instanceof AccessApiError) {
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

export function useTenantData<T>(slug: string, path: string, enabled = true) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [stepUp, setStepUp] = useState(false);
  const [loading, setLoading] = useState(true);
  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    setStepUp(false);
    try {
      setData(await apiRequest<T>(tenantApiPath(slug, path)));
    } catch (cause) {
      setError(errorMessage(cause));
      setStepUp(
        cause instanceof AccessApiError &&
          cause.type === 'urn:excellore:problem:step-up-required',
      );
    } finally {
      setLoading(false);
    }
  }, [slug, path]);
  useEffect(() => {
    if (enabled) void reload();
  }, [reload, enabled]);
  return { data, error, stepUp, loading, reload };
}

export function useTenantAction(slug: string) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [stepUp, setStepUp] = useState(false);
  async function run<T = unknown>(
    method: string,
    path: string,
    body: object | undefined,
    success: string,
    after?: () => void | Promise<void>,
  ): Promise<T | undefined> {
    setBusy(true);
    setMessage('');
    setStepUp(false);
    try {
      const result = await apiRequest<T>(
        tenantApiPath(slug, path),
        method,
        body,
      );
      setMessage(success);
      await after?.();
      return result;
    } catch (cause) {
      setMessage(errorMessage(cause));
      setStepUp(
        cause instanceof AccessApiError &&
          cause.type === 'urn:excellore:problem:step-up-required',
      );
    } finally {
      setBusy(false);
    }
  }
  return { busy, message, stepUp, run };
}
