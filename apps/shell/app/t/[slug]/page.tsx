import { notFound, redirect } from 'next/navigation';
import { SessionRequiredError, TenantAccessError } from '@excellore/auth';
import { getLauncherData } from '../../../lib/server';
import {
  identityInitials,
  ShellBanners,
  ShellHeader,
} from '../../shell-content';
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
        identity={data.identity}
      />
      <main id="main-content" tabIndex={-1} className="shell-dashboard">
        <ShellBanners
          invitations={data.invitations}
          invitationBase={data.invitationBase}
        />
        <section className="shell-workspace-hero">
          <div>
            <span className="shell-eyebrow">Current workspace</span>
            <h1>{data.tenant.name}</h1>
            <p>Choose an app to continue your work.</p>
          </div>
          <a className="shell-account-link" href={data.accountHref}>
            <span className="shell-avatar" aria-hidden="true">
              {identityInitials(data.identity.displayName)}
            </span>
            <span>
              <strong>{data.identity.displayName}</strong>
              <small>View your account</small>
            </span>
          </a>
        </section>
        {data.apps.length > 0 ? (
          <section aria-labelledby="apps-heading">
            <div className="shell-section-heading">
              <div>
                <span className="shell-eyebrow">Available to you</span>
                <h2 id="apps-heading">Your apps</h2>
              </div>
              <span className="shell-count">{data.apps.length}</span>
            </div>
            <ul className="ex-launcher">
              {data.apps.map((app) => (
                <li key={app.id}>
                  <a className="ex-tile ex-app-tile" href={app.href}>
                    <span
                      className={`ex-app-icon ex-app-icon--${app.id}`}
                      aria-hidden="true"
                    >
                      {app.id === 'access' ? 'A' : 'P'}
                    </span>
                    <span className="ex-tile__copy">
                      <strong>{app.name}</strong>
                      <span>{app.description}</span>
                    </span>
                    <span className="ex-tile__arrow" aria-hidden="true">
                      →
                    </span>
                  </a>
                </li>
              ))}
            </ul>
          </section>
        ) : (
          <section className="shell-empty">
            <h2>No apps are available yet</h2>
            <p>Ask your workspace administrator about your app access.</p>
          </section>
        )}
      </main>
    </>
  );
}
