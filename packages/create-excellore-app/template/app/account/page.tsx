import { redirect } from 'next/navigation';
import { SharedHeader } from '@excellore/ui';
import { SessionRequiredError } from '@excellore/auth';
import { getPageSession } from '../../lib/server';

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
      <SharedHeader
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
        <div className="ex-card">
          <h1 className="text-3xl font-semibold">Signed in</h1>
          <p className="mt-4">
            Your server-side session is active. Tenant workspaces will appear
            when the Access service is available.
          </p>
        </div>
      </main>
    </>
  );
}
