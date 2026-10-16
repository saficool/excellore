'use client';

import { useEffect } from 'react';
import { applyTheme } from './theme';

export function ThemeSync() {
  useEffect(() => {
    const controller = new AbortController();
    void fetch('/api/me/preferences', {
      credentials: 'same-origin',
      cache: 'no-store',
      signal: controller.signal,
    })
      .then((response) => (response.ok ? response.json() : null))
      .then((preferences: { theme?: unknown } | null) => {
        if (!controller.signal.aborted && preferences)
          applyTheme(preferences.theme);
      })
      .catch(() => {});
    return () => controller.abort();
  }, []);

  return null;
}
