import { notFound, redirect } from 'next/navigation';
import { SessionRequiredError, TenantAccessError } from '@excellore/auth';
import { getLauncherData } from '../../../lib/server';
import { ShellBanners, ShellHeader } from '../../shell-content';
import { RememberTenant } from '../../tenant-picker';

export const dynamic = 'force-dynamic';

export default async function TenantLauncher({
  params,
}: {
  params: Promise<{ slug: string }>;
}) {
  const { slug } = await params;
  let data;
  try {
    data = await getLauncherData(slug);
  } catch (error) {
    if (error instanceof SessionRequiredError) redirect(error.redirectTo);
    if (error instanceof TenantAccessError && error.status === 404) notFound();
    throw error;
  }
  return (
    <>
      <RememberTenant id={slug} />
      <ShellHeader
        accountHref={data.accountHref}
        tenants={data.tenants}
        apps={data.apps}
        currentTenant={{ slug, name: data.tenant.name }}
      />
      <main id="main-content" tabIndex={-1}>
        <ShellBanners
          invitations={data.invitations}
          invitationBase={data.invitationBase}
        />
        <h1 className="text-3xl font-semibold">{data.tenant.name}</h1>
        <p>Open an app in this tenant.</p>
        {data.apps.length > 0 ? (
          <ul className="ex-launcher">
            {data.apps.map((app) => (
              <li key={app.id}>
                <a className="ex-tile" href={app.href}>
                  <strong>{app.name}</strong>
                  <span>{app.description}</span>
                </a>
              </li>
            ))}
          </ul>
        ) : (
          <p>No apps are available for your membership.</p>
        )}
      </main>
    </>
  );
}
