import { InvitationAcceptance } from './invitation-acceptance';

export const dynamic = 'force-dynamic';

export default async function AcceptInvitationPage({
  searchParams,
}: {
  searchParams: Promise<{ id?: string }>;
}) {
  const { id } = await searchParams;
  return (
    <main id="main-content" tabIndex={-1} className="access-accept">
      <InvitationAcceptance id={id ?? ''} />
    </main>
  );
}
