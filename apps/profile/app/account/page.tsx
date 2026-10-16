import { redirect } from 'next/navigation';
import { SessionRequiredError } from '@excellore/auth';
import { getPageSession } from '../../lib/server';
import { ProfileHeader } from '../profile-header';
import { ProfileDashboard } from './profile-dashboard';

export const dynamic = 'force-dynamic';

export default async function AccountPage() {
  try {
    await getPageSession('/account');
  } catch (error) {
    if (error instanceof SessionRequiredError)
      redirect('/auth/login?returnTo=%2Faccount');
    throw error;
  }
  return (
    <>
      <ProfileHeader
        homeHref="/"
        tenants={[]}
        apps={[]}
        accountHref="/account"
        userLabel="Account"
        messages={{
          primaryNavigation: 'Navigation',
          switchTenant: 'Switch tenant',
          switchApp: 'Switch app',
          userMenu: 'Account menu',
          myAccount: 'My account',
          signOut: 'Sign out',
        }}
      />
      <main>
        <ProfileDashboard />
      </main>
    </>
  );
}
