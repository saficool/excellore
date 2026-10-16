'use client';

import { useEffect, useState } from 'react';

type Member = {
  userId: string;
  jobTitle: string | null;
  department: string | null;
  workPhone: string | null;
  directoryVisibility: 'visible' | 'hidden';
};

export function TenantProfile({
  slug,
  canManage,
}: {
  slug: string;
  canManage: boolean;
}) {
  const [own, setOwn] = useState<Member | null>(null);
  const [directory, setDirectory] = useState<Member[]>([]);
  const [message, setMessage] = useState('');
  const [editing, setEditing] = useState<Member | null>(null);
  const [reason, setReason] = useState('');
  const path = `/api/t/${slug}/members`;
  useEffect(() => {
    void fetch(`${path}/me`, { cache: 'no-store' })
      .then((response) => {
        if (!response.ok) throw new Error();
        return response.json() as Promise<Member>;
      })
      .then(setOwn)
      .catch(() => setMessage('Member profile is unavailable.'));
    void fetch(path, { cache: 'no-store' })
      .then((response) => {
        if (!response.ok) throw new Error();
        return response.json() as Promise<Member[]>;
      })
      .then(setDirectory)
      .catch(() => {
        // Members without directory permission may still edit their own profile.
      });
  }, [path]);

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!own) return;
    const response = await fetch(`${path}/me`, {
      method: 'PATCH',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Excellore-CSRF': '1' },
      body: JSON.stringify(own),
    });
    if (response.ok) {
      setOwn((await response.json()) as Member);
      setMessage('Member profile saved.');
    } else {
      setMessage('Could not save your member profile.');
    }
  }

  async function saveAdmin(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!editing || !reason.trim()) return;
    const response = await fetch(path, {
      method: 'PATCH',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Excellore-CSRF': '1' },
      body: JSON.stringify({ ...editing, reason }),
    });
    if (response.ok) {
      const updated = (await response.json()) as Member;
      setDirectory((current) =>
        current.map((item) =>
          item.userId === updated.userId ? updated : item,
        ),
      );
      setEditing(null);
      setReason('');
      setMessage('Member profile updated.');
    } else {
      setMessage('Could not update the member profile.');
    }
  }

  return (
    <div className="profile-grid">
      <section className="ex-card">
        <h2>My member profile</h2>
        {own ? (
          <form className="profile-form" onSubmit={(event) => void save(event)}>
            <label>
              Job title
              <input
                value={own.jobTitle ?? ''}
                maxLength={120}
                onChange={(event) =>
                  setOwn({ ...own, jobTitle: event.target.value })
                }
              />
            </label>
            <label>
              Department
              <input
                value={own.department ?? ''}
                maxLength={120}
                onChange={(event) =>
                  setOwn({ ...own, department: event.target.value })
                }
              />
            </label>
            <label>
              Work phone
              <input
                value={own.workPhone ?? ''}
                maxLength={40}
                onChange={(event) =>
                  setOwn({ ...own, workPhone: event.target.value })
                }
              />
            </label>
            <label>
              Directory visibility
              <select
                value={own.directoryVisibility}
                onChange={(event) =>
                  setOwn({
                    ...own,
                    directoryVisibility: event.target
                      .value as Member['directoryVisibility'],
                  })
                }
              >
                <option value="visible">Visible</option>
                <option value="hidden">Hidden</option>
              </select>
            </label>
            <button type="submit">Save member profile</button>
          </form>
        ) : (
          <p>Loading…</p>
        )}
      </section>
      <section className="ex-card">
        <h2>Member directory</h2>
        <p>Active members who opted into the directory.</p>
        {directory.length === 0 ? (
          <p>No visible members yet.</p>
        ) : (
          <ul>
            {directory.map((member) => (
              <li key={member.userId}>
                {member.jobTitle || 'Member'}
                {member.department ? ` · ${member.department}` : ''}
                {canManage && (
                  <button onClick={() => setEditing(member)}>Edit</button>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>
      {canManage && editing && (
        <section className="ex-card">
          <h2>Edit member</h2>
          <form
            className="profile-form"
            onSubmit={(event) => void saveAdmin(event)}
          >
            <label>
              Job title
              <input
                value={editing.jobTitle ?? ''}
                maxLength={120}
                onChange={(event) =>
                  setEditing({ ...editing, jobTitle: event.target.value })
                }
              />
            </label>
            <label>
              Department
              <input
                value={editing.department ?? ''}
                maxLength={120}
                onChange={(event) =>
                  setEditing({ ...editing, department: event.target.value })
                }
              />
            </label>
            <label>
              Work phone
              <input
                value={editing.workPhone ?? ''}
                maxLength={40}
                onChange={(event) =>
                  setEditing({ ...editing, workPhone: event.target.value })
                }
              />
            </label>
            <label>
              Reason
              <input
                value={reason}
                maxLength={500}
                required
                onChange={(event) => setReason(event.target.value)}
              />
            </label>
            <button type="submit">Save member</button>
          </form>
        </section>
      )}
      {message && (
        <p role="status" className="profile-status">
          {message}
        </p>
      )}
    </div>
  );
}
