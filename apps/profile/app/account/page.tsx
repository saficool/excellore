import { redirect } from 'next/navigation';
import { SessionRequiredError } from '@excellore/auth';
import { getAccountPageData } from '../../lib/server';
import { ProfileHeader } from '../profile-header';
import { ProfileDashboard } from './profile-dashboard';

export const dynamic = 'force-dynamic';

export default async function AccountPage() {
  let navigation;
  try {
    navigation = await getAccountPageData();
  } catch (error) {
    if (error instanceof SessionRequiredError)
      redirect('/auth/login?returnTo=%2Faccount');
    throw error;
  }
  return (
    <>
      <ProfileHeader
        homeHref={navigation.homeHref}
        tenants={[]}
        apps={[]}
        accountHref={navigation.accountHref}
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
        <ProfileDashboard />
      </main>
    </>
  );
}
