import { SharedHeader } from '@excellore/ui';
import type { PendingInvitation } from '@excellore/auth';
import { platformNotices } from '../lib/notices';

const messages = {
  primaryNavigation: 'Navigation',
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
}: {
  accountHref: string;
  tenants: readonly { id: string; name: string; href: string }[];
  apps?: readonly { id: string; name: string; href: string }[];
  currentTenant?: { slug: string; name: string };
}) {
  return (
    <SharedHeader
      homeHref="/"
      accountHref={accountHref}
      tenants={tenants}
      apps={apps}
      {...(currentTenant ? { currentTenant } : {})}
      userLabel="Signed in"
      messages={messages}
    />
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
