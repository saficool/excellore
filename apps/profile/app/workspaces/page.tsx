import { redirect } from 'next/navigation';
import { getSuiteNavigation } from '../../lib/server';

export const dynamic = 'force-dynamic';

export default function WorkspacesPage() {
  redirect(getSuiteNavigation().homeHref);
}
