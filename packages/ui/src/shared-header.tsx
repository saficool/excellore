export interface HeaderNavigationItem {
  id: string;
  name: string;
  href: string;
}

export interface HeaderMessages {
  primaryNavigation: string;
  switchTenant: string;
  switchApp: string;
  userMenu: string;
  myAccount: string;
  signOut: string;
}

export interface SharedHeaderProps {
  homeHref: string;
  currentTenant?: { slug: string; name: string };
  tenants: readonly HeaderNavigationItem[];
  apps: readonly HeaderNavigationItem[];
  currentAppId?: string;
  accountHref: string;
  userLabel: string;
  messages: HeaderMessages;
}

function checkedHref(href: string): string {
  if (
    href.startsWith('/') &&
    !href.startsWith('//') &&
    !href.includes('\\') &&
    !href.includes('?') &&
    !href.includes('#')
  )
    return href;
  let url: URL;
  try {
    url = new URL(href);
  } catch {
    throw new Error('Invalid navigation URL');
  }
  const local =
    url.protocol === 'http:' &&
    (url.hostname === 'localhost' || url.hostname.endsWith('.localhost'));
  if (
    (url.protocol !== 'https:' && !local) ||
    url.username ||
    url.password ||
    url.search ||
    url.hash
  )
    throw new Error('Invalid navigation URL');
  return url.href;
}

export function SharedHeader({
  homeHref,
  currentTenant,
  tenants,
  apps,
  currentAppId,
  accountHref,
  userLabel,
  messages,
}: SharedHeaderProps) {
  return (
    <header className="ex-header">
      <div className="ex-header__inner">
        <a className="ex-header__brand" href={checkedHref(homeHref)}>
          Excellore
        </a>
        {currentTenant && (
          <span className="ex-header__context">{currentTenant.name}</span>
        )}
        <nav className="ex-header__nav" aria-label={messages.primaryNavigation}>
          {currentTenant && (
            <details className="ex-menu">
              <summary>{messages.switchTenant}</summary>
              <ul className="ex-menu__items">
                {tenants.map((tenant) => (
                  <li key={tenant.id}>
                    <a
                      href={checkedHref(tenant.href)}
                      aria-current={
                        tenant.id === currentTenant.slug ? 'page' : undefined
                      }
                    >
                      {tenant.name}
                    </a>
                  </li>
                ))}
              </ul>
            </details>
          )}
          {currentTenant && (
            <details className="ex-menu">
              <summary>{messages.switchApp}</summary>
              <ul className="ex-menu__items">
                {apps.map((app) => (
                  <li key={app.id}>
                    <a
                      href={checkedHref(app.href)}
                      aria-current={
                        app.id === currentAppId ? 'page' : undefined
                      }
                    >
                      {app.name}
                    </a>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </nav>
        <details className="ex-menu ex-header__user">
          <summary aria-label={messages.userMenu}>{userLabel}</summary>
          <ul className="ex-menu__items">
            <li>
              <a href={checkedHref(accountHref)}>{messages.myAccount}</a>
            </li>
            <li>
              <form method="post" action="/auth/logout">
                <button type="submit">{messages.signOut}</button>
              </form>
            </li>
          </ul>
        </details>
      </div>
    </header>
  );
}
