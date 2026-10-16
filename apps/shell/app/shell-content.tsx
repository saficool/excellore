import { SharedHeader } from '@excellore/ui';
import type { PendingInvitation } from '@excellore/auth';
import { platformNotices } from '../lib/notices';

const messages = {
  skipToContent: 'Skip to content',
  primaryNavigation: 'Navigation',
  workspaces: 'Workspaces',
  switchTenant: 'Switch tenant',
  switchApp: 'Switch app',
  userMenu: 'Account menu',
  myAccount: 'My account',
  signOut: 'Sign out',
};

export function ShellHeader({
  accountHref,
  tenants,
  apps = [],
  currentTenant,
  identity,
}: {
  accountHref: string;
  tenants: readonly { id: string; name: string; href: string }[];
  apps?: readonly {
    id: string;
    name: string;
    description: string;
    href: string;
  }[];
  currentTenant?: { slug: string; name: string };
  identity: { displayName: string; email?: string | undefined };
}) {
  return (
    <SharedHeader
      homeHref="/"
      accountHref={accountHref}
      tenants={tenants}
      apps={apps}
      {...(currentTenant ? { currentTenant } : {})}
      userLabel={identity.displayName}
      userSecondaryLabel={identity.email}
      messages={messages}
    />
  );
}

export function identityInitials(label: string) {
  const parts = label.trim().split(/\s+/).filter(Boolean);
  return `${parts[0]?.[0] ?? 'E'}${parts.length > 1 ? (parts.at(-1)?.[0] ?? '') : (parts[0]?.[1] ?? '')}`.toUpperCase();
}

export function UserWelcome({
  identity,
  tenantCount,
  invitationCount,
}: {
  identity: { displayName: string; email?: string | undefined };
  tenantCount: number;
  invitationCount: number;
}) {
  const firstName =
    identity.displayName.split(/\s+/)[0] ?? identity.displayName;
  return (
    <section className="shell-welcome" aria-labelledby="welcome-heading">
      <div>
        <span className="shell-eyebrow">Your Excellore</span>
        <h1 id="welcome-heading">Welcome back, {firstName}.</h1>
        <p>Everything you can access, organized by workspace.</p>
      </div>
      <div className="shell-identity-card">
        <span className="shell-avatar" aria-hidden="true">
          {identityInitials(identity.displayName)}
        </span>
        <span>
          <strong>{identity.displayName}</strong>
          {identity.email && <small>{identity.email}</small>}
        </span>
        <dl>
          <div>
            <dt>Workspaces</dt>
            <dd>{tenantCount}</dd>
          </div>
          <div>
            <dt>Invitations</dt>
            <dd>{invitationCount}</dd>
          </div>
        </dl>
      </div>
    </section>
  );
}

export function ShellBanners({
  invitations,
  invitationBase,
}: {
  invitations: readonly PendingInvitation[];
  invitationBase: string;
}) {
  return (
    <>
      {platformNotices.map((notice) => (
        <aside className="ex-banner" key={notice.id} role="status">
          {notice.message}
        </aside>
      ))}
      {invitations.length > 0 && (
        <aside className="ex-banner" aria-label="Pending invitations">
          <h2>Pending invitations</h2>
          <ul>
            {invitations.map((invite) => {
              const href = `${invitationBase}?id=${encodeURIComponent(invite.id)}`;
              return (
                <li key={invite.id}>
                  <a href={href}>Accept invitation to {invite.tenantName}</a>
                </li>
              );
            })}
          </ul>
        </aside>
      )}
    </>
  );
}
