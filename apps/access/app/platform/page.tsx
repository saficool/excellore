import { notFound, redirect } from 'next/navigation';
import { SessionRequiredError } from '@excellore/auth';
import { getPlatformRoles } from '../../lib/server';
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
  return <PlatformConsole roles={roles} />;
}
