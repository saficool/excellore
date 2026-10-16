'use client';

import { useEffect, useState } from 'react';
import Image from 'next/image';
import { applyTheme } from '../theme';
import { AvatarInputError, prepareAvatar } from './prepare-avatar';

type OwnProfile = {
  firstName: string | null;
  lastName: string | null;
  locale: string | null;
  displayName: string | null;
  timeZone: string;
  hasAvatar: boolean;
};
type Preferences = Record<string, string | boolean>;
type OwnSession = {
  id: string;
  startedAt: string | null;
  lastActiveAt: string | null;
};

async function api<T>(
  path: string,
  method = 'GET',
  body?: unknown,
): Promise<T> {
  const response = await fetch(`/api/me/${path}`, {
    method,
    credentials: 'same-origin',
    cache: 'no-store',
    headers: {
      ...(method === 'GET'
        ? {}
        : {
            'Content-Type': 'application/json',
            'X-Excellore-CSRF': '1',
          }),
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  if (!response.ok) throw new Error(`Request failed (${response.status})`);
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export function ProfileDashboard() {
  const [profile, setProfile] = useState<OwnProfile | null>(null);
  const [preferences, setPreferences] = useState<Preferences>({});
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [sessions, setSessions] = useState<OwnSession[] | null>(null);
  const [exportJob, setExportJob] = useState<string | null>(null);
  const [avatarRevision, setAvatarRevision] = useState(0);
  const [avatarPreviewFailed, setAvatarPreviewFailed] = useState(false);
  const [avatarStatus, setAvatarStatus] = useState('');
  useEffect(() => {
    void Promise.all([
      api<OwnProfile>('profile'),
      api<Preferences>('preferences'),
    ])
      .then(([own, prefs]) => {
        setProfile(own);
        setPreferences(prefs);
        applyTheme(prefs.theme);
      })
      .catch(() => setMessage('Profile data is temporarily unavailable.'));
    void api<OwnSession[]>('sessions')
      .then(setSessions)
      .catch(() => setSessions(null));
  }, []);

  async function saveAccount(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!profile) return;
    setBusy(true);
    setMessage('');
    try {
      setProfile(await api<OwnProfile>('profile', 'PATCH', profile));
      setMessage('Account saved.');
    } catch {
      setMessage('Could not save your account.');
    } finally {
      setBusy(false);
    }
  }

  async function savePreferences(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setMessage('');
    try {
      setPreferences(
        await api<Preferences>('preferences', 'PATCH', preferences),
      );
      applyTheme(preferences.theme);
      setMessage('Preferences saved.');
    } catch {
      setMessage('Could not save preferences.');
    } finally {
      setBusy(false);
    }
  }

  async function requestExport() {
    setBusy(true);
    try {
      const result = await api<{ id: string }>('data-exports', 'POST', {});
      setExportJob(result.id);
      setMessage('Export requested.');
    } catch {
      setMessage('Could not request an export.');
    } finally {
      setBusy(false);
    }
  }

  async function revokeSession(id: string) {
    setBusy(true);
    try {
      const response = await fetch(`/api/me/sessions/${id}`, {
        method: 'DELETE',
        credentials: 'same-origin',
        headers: { 'X-Excellore-CSRF': '1' },
      });
      if (!response.ok) throw new Error();
      setSessions((current) => current?.filter((item) => item.id !== id) ?? []);
      setMessage('Session ended.');
    } catch {
      setMessage('Could not end the session.');
    } finally {
      setBusy(false);
    }
  }

  async function requestDeletion() {
    setBusy(true);
    try {
      const request = await api<{ id: string; status: string }>(
        'deletion-requests',
        'POST',
        {},
      );
      setMessage(
        request.status === 'pending_external_identity_deletion'
          ? 'Application data erased. External identity deletion is pending.'
          : 'Account deletion request submitted. Application erasure is pending.',
      );
    } catch {
      setMessage('A fresh MFA sign-in is required before requesting deletion.');
    } finally {
      setBusy(false);
    }
  }

  async function uploadAvatar(file: File) {
    setBusy(true);
    setAvatarStatus('Preparing image…');
    let stage: 'prepare' | 'upload' | 'process' = 'prepare';
    try {
      const prepared = await prepareAvatar(file);
      setAvatarStatus('Uploading image…');
      stage = 'upload';
      const upload = await api<{ id: string }>('avatar/uploads', 'POST', {
        contentType: prepared.type,
        sizeBytes: prepared.size,
      });
      const base64Data = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onerror = () => reject(new Error('Read failed'));
        reader.onload = () =>
          resolve(String(reader.result).split(',')[1] ?? '');
        reader.readAsDataURL(prepared);
      });
      stage = 'process';
      await api(`avatar/uploads/${upload.id}/complete`, 'POST', { base64Data });
      setProfile((current) => current && { ...current, hasAvatar: true });
      setAvatarPreviewFailed(false);
      setAvatarRevision((current) => current + 1);
      window.dispatchEvent(new Event('profile-avatar-changed'));
      setAvatarStatus('Avatar updated.');
    } catch (error) {
      const status =
        error instanceof Error
          ? /^Request failed \((\d{3})\)$/.exec(error.message)?.[1]
          : undefined;
      setAvatarStatus(
        error instanceof AvatarInputError
          ? error.message
          : `Could not ${stage === 'process' ? 'process' : 'upload'} the image${status ? ` (status ${status})` : ''}. Please try again.`,
      );
    } finally {
      setBusy(false);
    }
  }

  async function removeAvatar() {
    setBusy(true);
    try {
      const response = await fetch('/api/me/avatar', {
        method: 'DELETE',
        credentials: 'same-origin',
        headers: { 'X-Excellore-CSRF': '1' },
      });
      if (!response.ok) throw new Error();
      setProfile((current) => current && { ...current, hasAvatar: false });
      setAvatarPreviewFailed(false);
      window.dispatchEvent(new Event('profile-avatar-changed'));
      setAvatarStatus('Avatar removed.');
    } catch {
      setAvatarStatus('Could not remove the avatar. Please try again.');
    } finally {
      setBusy(false);
    }
  }

  if (!profile) return <p role="status">{message || 'Loading account…'}</p>;
  return (
    <div className="profile-grid">
      <section className="ex-card">
        <h1>My account</h1>
        <form
          onSubmit={(event) => void saveAccount(event)}
          className="profile-form"
        >
          <label>
            First name
            <input
              value={profile.firstName ?? ''}
              maxLength={80}
              onChange={(event) =>
                setProfile({ ...profile, firstName: event.target.value })
              }
            />
          </label>
          <label>
            Last name
            <input
              value={profile.lastName ?? ''}
              maxLength={80}
              onChange={(event) =>
                setProfile({ ...profile, lastName: event.target.value })
              }
            />
          </label>
          <label>
            Display name
            <input
              value={profile.displayName ?? ''}
              maxLength={120}
              onChange={(event) =>
                setProfile({ ...profile, displayName: event.target.value })
              }
            />
          </label>
          <label>
            Locale
            <input
              value={profile.locale ?? ''}
              maxLength={35}
              onChange={(event) =>
                setProfile({ ...profile, locale: event.target.value })
              }
            />
          </label>
          <label>
            Time zone
            <input
              value={profile.timeZone}
              maxLength={80}
              onChange={(event) =>
                setProfile({ ...profile, timeZone: event.target.value })
              }
            />
          </label>
          <button disabled={busy} type="submit">
            Save account
          </button>
        </form>
      </section>
      <section className="ex-card">
        <h2>Avatar</h2>
        {profile.hasAvatar && (
          <Image
            key={avatarRevision}
            src="/api/avatar"
            alt="Your avatar"
            width={128}
            height={128}
            unoptimized
            onError={() => setAvatarPreviewFailed(true)}
            onLoad={() => setAvatarPreviewFailed(false)}
          />
        )}
        {!profile.hasAvatar && <p>No avatar uploaded yet.</p>}
        {avatarPreviewFailed && (
          <p role="status">
            Avatar saved, but the preview could not load.{' '}
            <button
              type="button"
              onClick={() => {
                setAvatarPreviewFailed(false);
                setAvatarRevision((current) => current + 1);
              }}
            >
              Retry preview
            </button>
          </p>
        )}
        <label className="profile-file">
          Upload image
          <input
            type="file"
            accept="image/png,image/jpeg,image/webp"
            disabled={busy}
            onChange={(event) => {
              const file = event.target.files?.[0];
              event.currentTarget.value = '';
              if (file) void uploadAvatar(file);
            }}
          />
        </label>
        <p className="profile-hint">
          PNG, JPEG, or WebP, up to 10 MB. Large images are resized before
          upload.
        </p>
        {avatarStatus && <p role="status">{avatarStatus}</p>}
        {profile.hasAvatar && (
          <button disabled={busy} onClick={() => void removeAvatar()}>
            Remove avatar
          </button>
        )}
      </section>
      <section className="ex-card">
        <h2>Preferences</h2>
        <form
          onSubmit={(event) => void savePreferences(event)}
          className="profile-form"
        >
          <label>
            Language
            <input
              value={String(preferences.language ?? '')}
              maxLength={35}
              onChange={(event) =>
                setPreferences({ ...preferences, language: event.target.value })
              }
            />
          </label>
          <label>
            Date format
            <select
              value={String(preferences.dateFormat ?? 'local')}
              onChange={(event) =>
                setPreferences({
                  ...preferences,
                  dateFormat: event.target.value,
                })
              }
            >
              <option value="local">Local</option>
              <option value="iso">ISO</option>
            </select>
          </label>
          <label>
            Theme
            <select
              value={String(preferences.theme ?? 'system')}
              onChange={(event) => {
                const theme = event.target.value;
                setPreferences({ ...preferences, theme });
                applyTheme(theme);
              }}
            >
              <option value="system">System</option>
              <option value="light">Light</option>
              <option value="dark">Dark</option>
            </select>
          </label>
          <label className="profile-check">
            <input
              type="checkbox"
              checked={preferences.notifications !== false}
              onChange={(event) =>
                setPreferences({
                  ...preferences,
                  notifications: event.target.checked,
                })
              }
            />
            Notifications
          </label>
          <button disabled={busy} type="submit">
            Save preferences
          </button>
        </form>
      </section>
      <section className="ex-card">
        <h2>Security</h2>
        <p>Manage credentials in the identity provider.</p>
        <div className="profile-actions">
          <a href="/auth/account-action?action=UPDATE_PASSWORD&returnTo=%2Faccount">
            Change password
          </a>
          <a href="/auth/account-action?action=CONFIGURE_TOTP&returnTo=%2Faccount">
            Manage authenticator
          </a>
          <a href="/auth/account-action?action=UPDATE_EMAIL&returnTo=%2Faccount">
            Change email
          </a>
        </div>
      </section>
      <section className="ex-card">
        <h2>Active sessions</h2>
        {sessions === null ? (
          <p>Sessions are unavailable.</p>
        ) : sessions.length === 0 ? (
          <p>No active sessions found.</p>
        ) : (
          <ul>
            {sessions.map((session) => (
              <li key={session.id}>
                {session.lastActiveAt
                  ? `Last active ${new Date(session.lastActiveAt).toLocaleString()}`
                  : 'Active session'}{' '}
                <button
                  disabled={busy}
                  onClick={() => void revokeSession(session.id)}
                >
                  End session
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
      <section className="ex-card">
        <h2>My data</h2>
        <button disabled={busy} onClick={() => void requestExport()}>
          Request data export
        </button>
        {exportJob && <p>Export request queued.</p>}
        <p>Account deletion requires a fresh MFA sign-in.</p>
        <a href="/auth/step-up?returnTo=%2Faccount">Verify with MFA</a>
        <p>
          <button disabled={busy} onClick={() => void requestDeletion()}>
            Request account deletion
          </button>
        </p>
      </section>
      {message && (
        <p role="status" className="profile-status">
          {message}
        </p>
      )}
    </div>
  );
}
