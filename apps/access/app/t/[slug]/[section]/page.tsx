import { notFound, redirect } from 'next/navigation';
import { SharedHeader } from '@excellore/ui';
import { SessionRequiredError, TenantAccessError } from '@excellore/auth';
import { getTenantPageData } from '../../../../lib/server';
import { sectionAllowed, visibleSections } from '../../../../lib/console';
import { ConsoleSection } from '../console-section';

export const dynamic = 'force-dynamic';

export default async function SectionPage({
  params,
}: {
  params: Promise<{ slug: string; section: string }>;
}) {
  const { slug, section } = await params;
  let data;
  try {
    data = await getTenantPageData(slug);
  } catch (error) {
    if (error instanceof SessionRequiredError) redirect(error.redirectTo);
    if (error instanceof TenantAccessError && error.status === 404) notFound();
    throw error;
  }
  if (!sectionAllowed(section, data.tenant.permissions)) notFound();
  const nav = visibleSections(data.tenant.permissions);
  return (
    <>
      <SharedHeader
        homeHref="/"
        currentTenant={{ slug, name: data.tenant.name }}
        tenants={data.tenants}
        apps={data.apps}
        currentAppId="access"
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
      <div className="access-layout">
        <nav className="access-sidebar" aria-label="Access sections">
          {nav.map((item) => (
            <a
              key={item.id}
              href={`/t/${encodeURIComponent(slug)}/${item.id}`}
              aria-current={item.id === section ? 'page' : undefined}
            >
              {item.label}
            </a>
          ))}
        </nav>
        <main className="access-main">
          <ConsoleSection
            section={section}
            slug={slug}
            tenantName={data.tenant.name}
            permissions={data.tenant.permissions}
          />
        </main>
      </div>
    </>
  );
}
