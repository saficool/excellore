'use client';

import { UnexpectedError } from '@excellore/ui';

export default function ErrorPage({ reset }: { reset: () => void }) {
  return <UnexpectedError reset={reset} />;
}
