import { notFound, redirect } from 'next/navigation';
import { SessionRequiredError, TenantAccessError } from '@excellore/auth';
import { getTenantPageData } from '../../../lib/server';
import { ProfileHeader } from '../../profile-header';
import { TenantProfile } from './tenant-profile';

export const dynamic = 'force-dynamic';

export default async function TenantPage({
  params,
}: {
  params: Promise<{ slug: string }>;
}) {
  const { slug } = await params;
  let data;
  try {
    data = await getTenantPageData(slug);
  } catch (error) {
    if (error instanceof SessionRequiredError) redirect(error.redirectTo);
    if (error instanceof TenantAccessError && error.status === 404) notFound();
    throw error;
  }
  return (
    <>
      <ProfileHeader
        homeHref={data.homeHref}
        currentTenant={{ slug, name: data.tenant.name }}
        tenants={data.tenants}
        apps={data.apps}
        currentAppId="profile"
        accountHref={data.accountHref}
        userLabel="Account"
        messages={{
          skipToContent: 'Skip to content',
          primaryNavigation: 'Navigation',
          workspaces: 'Workspaces',
          switchTenant: 'Switch tenant',
          switchApp: 'Switch app',
          userMenu: 'Account menu',
          myAccount: 'My account',
          signOut: 'Sign out',
        }}
      />
      <main id="main-content" tabIndex={-1}>
        <h1>{data.tenant.name} profiles</h1>
        <TenantProfile
          slug={slug}
          canManage={data.tenant.permissions.includes('profile.member.manage')}
        />
      </main>
    </>
  );
}
