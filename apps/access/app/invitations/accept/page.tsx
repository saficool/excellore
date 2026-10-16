import { InvitationAcceptance } from './invitation-acceptance';

export const dynamic = 'force-dynamic';

export default async function AcceptInvitationPage({
  searchParams,
}: {
  searchParams: Promise<{ id?: string }>;
}) {
  const { id } = await searchParams;
  return (
    <main className="access-accept">
      <InvitationAcceptance id={id ?? ''} />
    </main>
  );
}
