'use client';

import { useEffect, useState, type FormEvent } from 'react';
import { apiRequest, errorMessage } from '../../../lib/browser-api';

const invitationIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function InvitationAcceptance({ id }: { id: string }) {
  const [invitationId, setInvitationId] = useState('');
  const [authenticated, setAuthenticated] = useState<boolean | null>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [accepted, setAccepted] = useState(false);

  useEffect(() => {
    if (invitationIdPattern.test(id)) {
      sessionStorage.setItem('excellore:pending-invitation-id', id);
      history.replaceState(null, '', '/invitations/accept');
    }
    setInvitationId(
      sessionStorage.getItem('excellore:pending-invitation-id') ?? '',
    );
    void fetch('/auth/session', {
      cache: 'no-store',
      credentials: 'same-origin',
    })
      .then((response) => response.json())
      .then((value) => setAuthenticated(value.authenticated === true))
      .catch(() => setAuthenticated(false));
  }, [id]);

  async function accept(event: FormEvent) {
    event.preventDefault();
    if (!invitationIdPattern.test(invitationId)) {
      setMessage('Enter a valid invitation ID.');
      return;
    }
    setBusy(true);
    setMessage('');
    try {
      await apiRequest('/api/me/invitations/accept', 'POST', {
        invitationId,
        oneTimeCode: code,
      });
      sessionStorage.removeItem('excellore:pending-invitation-id');
      setCode('');
      setAccepted(true);
      setMessage('Invitation accepted. Open your tenant from the Shell.');
    } catch (cause) {
      setMessage(errorMessage(cause));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="ex-card access-card">
      <h1>Accept an invitation</h1>
      <p>
        Sign in with the invited account, then enter the one-time code from your
        email.
      </p>
      {authenticated === false && (
        <a
          className="access-button"
          href="/auth/login?returnTo=%2Finvitations%2Faccept"
        >
          Sign in
        </a>
      )}
      {authenticated && !accepted && (
        <form className="access-form" onSubmit={accept}>
          <label>
            Invitation ID
            <input
              value={invitationId}
              onChange={(e) => setInvitationId(e.target.value)}
              required
              pattern="[0-9a-fA-F-]{36}"
            />
          </label>
          <label>
            One-time code
            <input
              type="text"
              autoComplete="one-time-code"
              value={code}
              onChange={(e) => setCode(e.target.value)}
              required
            />
          </label>
          <button disabled={busy} type="submit">
            Accept invitation
          </button>
        </form>
      )}
      {message && <p role="status">{message}</p>}
    </div>
  );
}
