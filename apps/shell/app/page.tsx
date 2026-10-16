import { redirect } from 'next/navigation';
import { SessionRequiredError } from '@excellore/auth';
import {
  getPickerData,
  getPublicNavigation,
  hasShellSessionCookie,
} from '../lib/server';
import { PublicHome } from './public-home';
import { ShellBanners, ShellHeader, UserWelcome } from './shell-content';
import { TenantPicker } from './tenant-picker';

export const dynamic = 'force-dynamic';

export default async function Home() {
  const publicNavigation = getPublicNavigation();
  if (!(await hasShellSessionCookie()))
    return <PublicHome joinHref={publicNavigation.invitationBase} />;

  let data;
  try {
    data = await getPickerData();
  } catch (error) {
    if (error instanceof SessionRequiredError)
      return <PublicHome joinHref={publicNavigation.invitationBase} />;
    throw error;
  }
  if (data.tenants.length === 1) redirect(data.tenants[0]!.href);
  return (
    <>
      <ShellHeader
        accountHref={data.accountHref}
        tenants={data.tenants}
        identity={data.identity}
      />
      <main id="main-content" tabIndex={-1} className="shell-dashboard">
        <ShellBanners
          invitations={data.invitations}
          invitationBase={data.invitationBase}
        />
        <UserWelcome
          identity={data.identity}
          tenantCount={data.tenants.length}
          invitationCount={data.invitations.length}
        />
        {data.tenants.length > 0 ? (
          <section aria-labelledby="workspaces-heading">
            <div className="shell-section-heading">
              <div>
                <span className="shell-eyebrow">Your organizations</span>
                <h2 id="workspaces-heading">Choose a workspace</h2>
              </div>
              <span className="shell-count">{data.tenants.length}</span>
            </div>
            <TenantPicker tenants={data.tenants} />
          </section>
        ) : (
          <section className="shell-empty">
            <span className="shell-empty__icon" aria-hidden="true">
              +
            </span>
            <h2>Your first workspace is waiting</h2>
            <p>Accept an invitation from your organization to get started.</p>
            <a href={data.invitationBase}>Join with an invitation</a>
          </section>
        )}
      </main>
    </>
  );
}
