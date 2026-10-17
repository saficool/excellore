import { notFound, redirect } from 'next/navigation';
import { SessionRequiredError } from '@excellore/auth';
import { loadPlatformStaff } from '@excellore/config/server';
import { resolve } from 'node:path';
import { getPlatformRoles, profileAccountUrl } from '../lib/server';
import { PlatformConsole } from './platform-console';

export const dynamic = 'force-dynamic';

export default async function PlatformPage() {
  let roles: string[] | null;
  try {
    roles = await getPlatformRoles();
  } catch (error) {
    if (error instanceof SessionRequiredError) redirect(error.redirectTo);
    throw error;
  }
  if (!roles) notFound();
  const staff = loadPlatformStaff(
    resolve(process.cwd(), '../../infra/keycloak/platform-staff.yaml'),
  );
  return (
    <PlatformConsole
      roles={roles}
      staff={staff}
      accountUrl={profileAccountUrl()}
    />
  );
}
