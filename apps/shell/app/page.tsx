import { redirect } from 'next/navigation';
import { SessionRequiredError } from '@excellore/auth';
import { getPickerData } from '../lib/server';
import { ShellBanners, ShellHeader } from './shell-content';
import { TenantPicker } from './tenant-picker';

export const dynamic = 'force-dynamic';

export default async function Home() {
  let data;
  try {
    data = await getPickerData();
  } catch (error) {
    if (error instanceof SessionRequiredError) redirect(error.redirectTo);
    throw error;
  }
  if (data.tenants.length === 1) redirect(data.tenants[0]!.href);
  return (
    <>
      <ShellHeader accountHref={data.accountHref} tenants={data.tenants} />
      <main id="main-content" tabIndex={-1}>
        <ShellBanners
          invitations={data.invitations}
          invitationBase={data.invitationBase}
        />
        <h1 className="text-3xl font-semibold">Choose a tenant</h1>
        {data.tenants.length > 0 ? (
          <TenantPicker tenants={data.tenants} />
        ) : (
          <p>
            No active tenants are available. Accept an invitation to get
            started.
          </p>
        )}
      </main>
    </>
  );
}
