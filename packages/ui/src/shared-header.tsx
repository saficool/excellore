export interface HeaderNavigationItem {
  id: string;
  name: string;
  href: string;
}

export interface HeaderMessages {
  skipToContent: string;
  primaryNavigation: string;
  workspaces: string;
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
  userSecondaryLabel?: string | undefined;
  avatarSrc?: string | undefined;
  avatarRevision?: number;
  messages: HeaderMessages;
}

function userInitials(label: string): string {
  const parts = label.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return 'EX';
  return `${parts[0]?.[0] ?? ''}${parts.length > 1 ? (parts.at(-1)?.[0] ?? '') : (parts[0]?.[1] ?? '')}`.toUpperCase();
}

function checkedAvatarSrc(src: string): string {
  if (
    !src.startsWith('/') ||
    src.startsWith('//') ||
    src.includes('\\') ||
    src.includes('?') ||
    src.includes('#')
  )
    throw new Error('Invalid avatar URL');
  return src;
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
  userSecondaryLabel,
  avatarSrc,
  avatarRevision,
  messages,
}: SharedHeaderProps) {
  return (
    <>
      <a className="ex-skip-link" href="#main-content">
        {messages.skipToContent}
      </a>
      <header className="ex-header">
        <div className="ex-header__inner">
          <a className="ex-header__brand" href={checkedHref(homeHref)}>
            Excellore
          </a>
          {currentTenant && (
            <span className="ex-header__context">{currentTenant.name}</span>
          )}
          <nav
            className="ex-header__nav"
            aria-label={messages.primaryNavigation}
          >
            <a className="ex-header__workspaces" href={checkedHref(homeHref)}>
              {messages.workspaces}
            </a>
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
            <summary aria-label={messages.userMenu}>
              {avatarSrc ? (
                <img
                  key={avatarRevision}
                  className="ex-header__avatar"
                  src={checkedAvatarSrc(avatarSrc)}
                  alt=""
                  width={32}
                  height={32}
                />
              ) : (
                <span className="ex-header__avatar-fallback" aria-hidden="true">
                  {userInitials(userLabel)}
                </span>
              )}
              <span className="ex-header__user-copy">
                <strong>{userLabel}</strong>
                {userSecondaryLabel && <small>{userSecondaryLabel}</small>}
              </span>
            </summary>
            <ul className="ex-menu__items">
              {userSecondaryLabel && (
                <li className="ex-menu__identity">
                  <strong>{userLabel}</strong>
                  <span>{userSecondaryLabel}</span>
                </li>
              )}
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
    </>
  );
}
