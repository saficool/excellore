'use client';

export default function ErrorPage({ reset }: { reset: () => void }) {
  return (
    <main>
      <h1>Platform console unavailable</h1>
      <p>The console could not be loaded. Please try again.</p>
      <button type="button" onClick={reset}>
        Try again
      </button>
    </main>
  );
}
