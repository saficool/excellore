'use client';

function checkedHref(href: string): string {
  if (
    href.startsWith('/') &&
    !href.startsWith('//') &&
    !href.includes('\\') &&
    !href.includes('?') &&
    !href.includes('#')
  )
    return href;
  throw new Error('Invalid page state URL');
}

export function UnexpectedError({ reset }: { reset: () => void }) {
  return (
    <main id="main-content" className="ex-page-state" tabIndex={-1}>
      <p className="ex-page-state__eyebrow">Excellore</p>
      <h1>Something went wrong</h1>
      <p>
        We could not load this page. Your changes were not submitted unless you
        already saw a success message.
      </p>
      <div className="ex-page-state__actions">
        <button type="button" onClick={reset}>
          Try again
        </button>
        <a href="/">Return to start</a>
      </div>
    </main>
  );
}

export function NotFound({ homeHref = '/' }: { homeHref?: string }) {
  return (
    <main id="main-content" className="ex-page-state" tabIndex={-1}>
      <p className="ex-page-state__eyebrow">Excellore</p>
      <h1>Page not found</h1>
      <p>
        This page does not exist, or you do not have access to its workspace.
      </p>
      <div className="ex-page-state__actions">
        <a href={checkedHref(homeHref)}>Return to workspaces</a>
      </div>
    </main>
  );
}
