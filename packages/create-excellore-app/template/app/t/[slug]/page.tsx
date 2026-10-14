import { notFound, redirect } from 'next/navigation';
import { SharedHeader } from '@excellore/ui';
import { SessionRequiredError, TenantAccessError } from '@excellore/auth';
import { getTenantPageData } from '../../../lib/server';

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
      <SharedHeader
        homeHref="/"
        currentTenant={{ slug, name: data.tenant.name }}
        tenants={data.tenants}
        apps={data.apps}
        currentAppId="__APP_ID__"
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
          <h1 className="text-3xl font-semibold">{data.tenant.name}</h1>
          <p className="mt-4">__APP_TITLE__ workspace</p>
        </div>
      </main>
    </>
  );
}
