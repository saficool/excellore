'use client';

import { useState, type FormEvent, type ReactNode } from 'react';
import {
  tenantApiPath,
  useTenantAction,
  useTenantData,
} from '../../../lib/browser-api';

type Member = {
  userId: string;
  membershipId: string;
  status: string;
  email: string;
  givenName: string | null;
  familyName: string | null;
  joinedAt: string | null;
};
type Role = {
  id: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  privileged: boolean;
  permissions: string[];
};
type Permission = {
  key: string;
  app: string;
  description: string;
  privileged: boolean;
  ownerOnly: boolean;
};
type Invitation = {
  id: string;
  email: string;
  roleIds: string[];
  status: string;
  expiresAt: string;
};
type Assignment = {
  id: string;
  userId: string;
  roleId: string;
  roleName: string;
  expiresAt: string | null;
  createdAt: string;
};
type Audit = {
  id: string;
  occurredAt: string;
  actorType: string;
  action: string;
  targetType: string | null;
  reason: string | null;
};
type SupportGrant = {
  id: string;
  staffEmail: string;
  roleName: string;
  reason: string;
  status: string;
  expiresAt: string;
  createdAt: string;
};
type AuditExport = {
  id: string;
  status: string;
  createdAt: string;
  readyAt: string | null;
};
type Settings = { name: string; slug: string; plan: string };

function Card({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="ex-card access-card">
      <h2>{title}</h2>
      {children}
    </section>
  );
}

function DataState({
  loading,
  error,
  stepUp = false,
}: {
  loading: boolean;
  error: string | null;
  stepUp?: boolean;
}) {
  if (loading) return <p role="status">Loading…</p>;
  if (error)
    return (
      <div role="alert">
        <p>{error}</p>
        {stepUp && (
          <a
            href={`/auth/step-up?returnTo=${encodeURIComponent(window.location.pathname)}`}
          >
            Confirm with MFA
          </a>
        )}
      </div>
    );
  return null;
}

function Feedback({ action }: { action: ReturnType<typeof useTenantAction> }) {
  if (!action.message) return null;
  return (
    <div className="access-feedback" role="status">
      <p>{action.message}</p>
      {action.stepUp && (
        <a
          href={`/auth/step-up?returnTo=${encodeURIComponent(window.location.pathname)}`}
        >
          Confirm with MFA
        </a>
      )}
    </div>
  );
}

function ReasonField({
  value,
  onChange,
}: {
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <label>
      Reason code
      <input
        value={value}
        onChange={(event) => onChange(event.target.value)}
        pattern="[a-z][a-z0-9._-]{0,63}"
        maxLength={64}
        required
        title="Lowercase letters, digits, dots, underscores, or hyphens"
      />
    </label>
  );
}

function MyAccess({ slug }: { slug: string }) {
  const { data, loading, error } = useTenantData<{
    roles: string[];
    permissions: string[];
    apps: string[];
  }>(slug, 'me/access');
  return (
    <Card title="My access">
      <DataState loading={loading} error={error} />
      {data && (
        <div className="access-columns">
          <div>
            <h3>Roles</h3>
            <ul>
              {data.roles.map((role) => (
                <li key={role}>{role}</li>
              ))}
            </ul>
          </div>
          <div>
            <h3>Apps</h3>
            <ul>
              {data.apps.map((app) => (
                <li key={app}>{app}</li>
              ))}
            </ul>
          </div>
          <div>
            <h3>Permissions</h3>
            <ul>
              {data.permissions.map((key) => (
                <li key={key}>{key}</li>
              ))}
            </ul>
          </div>
        </div>
      )}
    </Card>
  );
}

function Members({
  slug,
  permissions,
}: {
  slug: string;
  permissions: readonly string[];
}) {
  const { data, loading, error, reload } = useTenantData<Member[]>(
    slug,
    'members',
  );
  const action = useTenantAction(slug);
  const [reason, setReason] = useState('administrative_change');
  const [filter, setFilter] = useState('');
  const [statusFilter, setStatusFilter] = useState('all');
  const [selectedMember, setSelectedMember] = useState<Member | null>(null);
  const canSuspend = permissions.includes('access.member.suspend');
  const canRemove = permissions.includes('access.member.remove');
  return (
    <Card title="Members">
      <DataState loading={loading} error={error} />
      {data?.length === 0 && <p>No members found.</p>}
      {data && data.length > 0 && (
        <>
          <div className="access-filters">
            <label>
              Search members
              <input
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder="Name or email"
              />
            </label>
            <label>
              Status
              <select
                value={statusFilter}
                onChange={(e) => setStatusFilter(e.target.value)}
              >
                <option value="all">All statuses</option>
                <option value="active">Active</option>
                <option value="suspended">Suspended</option>
              </select>
            </label>
          </div>
          {(canSuspend || canRemove) && (
            <ReasonField value={reason} onChange={setReason} />
          )}
          <div className="access-table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Email</th>
                  <th>Status</th>
                  <th>Joined</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {data
                  .filter(
                    (member) =>
                      (statusFilter === 'all' ||
                        member.status === statusFilter) &&
                      `${member.email} ${member.givenName ?? ''} ${member.familyName ?? ''}`
                        .toLocaleLowerCase()
                        .includes(filter.toLocaleLowerCase()),
                  )
                  .map((member) => (
                    <tr key={member.membershipId}>
                      <td>
                        {[member.givenName, member.familyName]
                          .filter(Boolean)
                          .join(' ') || '—'}
                      </td>
                      <td>{member.email}</td>
                      <td>{member.status}</td>
                      <td>
                        {member.joinedAt
                          ? new Date(member.joinedAt).toLocaleDateString()
                          : '—'}
                      </td>
                      <td className="access-actions">
                        <button onClick={() => setSelectedMember(member)}>
                          Details
                        </button>
                        {canSuspend && (
                          <button
                            disabled={action.busy}
                            onClick={() =>
                              void action.run(
                                'POST',
                                member.status === 'suspended'
                                  ? 'members:reactivate'
                                  : 'members:suspend',
                                { userId: member.userId, reason },
                                'Member updated.',
                                reload,
                              )
                            }
                          >
                            {member.status === 'suspended'
                              ? 'Reactivate'
                              : 'Suspend'}
                          </button>
                        )}
                        {canRemove && (
                          <button
                            disabled={action.busy}
                            onClick={() => {
                              if (
                                window.confirm(
                                  'Remove this member from this tenant?',
                                )
                              )
                                void action.run(
                                  'DELETE',
                                  'members',
                                  { userId: member.userId, reason },
                                  'Member removed.',
                                  reload,
                                );
                            }}
                          >
                            Remove
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
              </tbody>
            </table>
          </div>
          {selectedMember && (
            <div
              className="access-detail"
              role="region"
              aria-label="Member details"
            >
              <button onClick={() => setSelectedMember(null)}>Close</button>
              <h3>Member details</h3>
              <dl>
                <dt>Name</dt>
                <dd>
                  {[selectedMember.givenName, selectedMember.familyName]
                    .filter(Boolean)
                    .join(' ') || '—'}
                </dd>
                <dt>Email</dt>
                <dd>{selectedMember.email}</dd>
                <dt>Status</dt>
                <dd>{selectedMember.status}</dd>
                <dt>Joined</dt>
                <dd>
                  {selectedMember.joinedAt
                    ? new Date(selectedMember.joinedAt).toLocaleString()
                    : '—'}
                </dd>
              </dl>
            </div>
          )}
        </>
      )}
      <Feedback action={action} />
    </Card>
  );
}

function Invitations({ slug }: { slug: string }) {
  const { data, loading, error, reload } = useTenantData<Invitation[]>(
    slug,
    'invitations',
  );
  const roles = useTenantData<Role[]>(slug, 'roles');
  const action = useTenantAction(slug);
  const [email, setEmail] = useState('');
  const [roleId, setRoleId] = useState('');
  const [reason, setReason] = useState('member_invitation');
  function invite(event: FormEvent) {
    event.preventDefault();
    void action.run(
      'POST',
      'invitations',
      { email, roleIds: [roleId], groupIds: [], reason },
      'Invitation queued for delivery.',
      async () => {
        setEmail('');
        await reload();
      },
    );
  }
  return (
    <div className="access-stack">
      <Card title="Invite a member">
        <p>
          The invitation link and one-time code are sent separately by email.
        </p>
        <form className="access-form" onSubmit={invite}>
          <label>
            Email
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
            />
          </label>
          <label>
            Role
            <select
              value={roleId}
              onChange={(e) => setRoleId(e.target.value)}
              required
            >
              <option value="">Choose a role</option>
              {roles.data
                ?.filter((role) => role.name !== 'owner')
                .map((role) => (
                  <option key={role.id} value={role.id}>
                    {role.name}
                  </option>
                ))}
            </select>
          </label>
          <ReasonField value={reason} onChange={setReason} />
          <button disabled={action.busy || !roleId} type="submit">
            Send invitation
          </button>
        </form>
        <DataState loading={roles.loading} error={roles.error} />
        <Feedback action={action} />
      </Card>
      <Card title="Invitations">
        <DataState loading={loading} error={error} />
        {data?.length === 0 && <p>No invitations found.</p>}
        {data?.map((invitation) => (
          <div className="access-row" key={invitation.id}>
            <span>
              {invitation.email} · {invitation.status} · Expires{' '}
              {new Date(invitation.expiresAt).toLocaleDateString()}
            </span>
            <div className="access-actions">
              <button
                disabled={action.busy}
                onClick={() =>
                  void action.run(
                    'POST',
                    `invitations/${invitation.id}/resend`,
                    undefined,
                    'Invitation resent.',
                    reload,
                  )
                }
              >
                Resend
              </button>
              <button
                disabled={action.busy}
                onClick={() =>
                  void action.run(
                    'DELETE',
                    `invitations/${invitation.id}`,
                    undefined,
                    'Invitation revoked.',
                    reload,
                  )
                }
              >
                Revoke
              </button>
            </div>
          </div>
        ))}
      </Card>
    </div>
  );
}

function Roles({ slug, canManage }: { slug: string; canManage: boolean }) {
  const roles = useTenantData<Role[]>(slug, 'roles');
  const catalog = useTenantData<Permission[]>(slug, 'permissions');
  const action = useTenantAction(slug);
  const [editing, setEditing] = useState<Role | null>(null);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [selected, setSelected] = useState<string[]>([]);
  function select(role: Role | null) {
    setEditing(role);
    setName(role?.name ?? '');
    setDescription(role?.description ?? '');
    setSelected(role?.permissions ?? []);
  }
  function save(event: FormEvent) {
    event.preventDefault();
    void action.run(
      editing ? 'PUT' : 'POST',
      editing ? `roles/${editing.id}` : 'roles',
      { name, description, permissions: selected },
      editing ? 'Role updated.' : 'Role created.',
      async () => {
        select(null);
        await roles.reload();
      },
    );
  }
  return (
    <div className="access-stack">
      <Card title="Roles">
        <DataState loading={roles.loading} error={roles.error} />
        {roles.data?.map((role) => (
          <div className="access-row" key={role.id}>
            <span>
              <strong>{role.name}</strong>{' '}
              {role.isSystem ? '· System' : '· Custom'}
              {role.privileged ? ' · Privileged' : ''}
              <br />
              {role.permissions.join(', ')}
            </span>
            {canManage && !role.isSystem && (
              <button onClick={() => select(role)}>Edit</button>
            )}
          </div>
        ))}
      </Card>
      {canManage && (
        <Card title={editing ? 'Edit custom role' : 'Create custom role'}>
          <form className="access-form" onSubmit={save}>
            <label>
              Role name
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                required
                maxLength={80}
              />
            </label>
            <label>
              Description
              <input
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                maxLength={500}
              />
            </label>
            <fieldset>
              <legend>Permissions</legend>
              <DataState loading={catalog.loading} error={catalog.error} />
              {[...new Set(catalog.data?.map((item) => item.app) ?? [])].map(
                (app) => (
                  <div key={app}>
                    <h3>{app}</h3>
                    {catalog.data
                      ?.filter((item) => item.app === app && !item.ownerOnly)
                      .map((item) => (
                        <label className="access-check" key={item.key}>
                          <input
                            type="checkbox"
                            checked={selected.includes(item.key)}
                            onChange={(e) =>
                              setSelected((current) =>
                                e.target.checked
                                  ? [...current, item.key]
                                  : current.filter((key) => key !== item.key),
                              )
                            }
                          />
                          <span>
                            <strong>{item.key}</strong> · {item.description}
                            {item.privileged ? ' · Requires MFA' : ''}
                          </span>
                        </label>
                      ))}
                  </div>
                ),
              )}
            </fieldset>
            <div className="access-actions">
              <button type="submit" disabled={action.busy}>
                Save role
              </button>
              {editing && (
                <>
                  <button type="button" onClick={() => select(null)}>
                    Cancel
                  </button>
                  <button
                    type="button"
                    disabled={action.busy}
                    onClick={() => {
                      if (window.confirm('Delete this custom role?'))
                        void action.run(
                          'DELETE',
                          `roles/${editing.id}`,
                          undefined,
                          'Role deleted.',
                          async () => {
                            select(null);
                            await roles.reload();
                          },
                        );
                    }}
                  >
                    Delete role
                  </button>
                </>
              )}
            </div>
          </form>
          <Feedback action={action} />
        </Card>
      )}
    </div>
  );
}

function Assignments({ slug }: { slug: string }) {
  const members = useTenantData<Member[]>(slug, 'members');
  const roles = useTenantData<Role[]>(slug, 'roles');
  const assignments = useTenantData<Assignment[]>(slug, 'role-assignments');
  const action = useTenantAction(slug);
  const [userId, setUserId] = useState('');
  const [roleId, setRoleId] = useState('');
  const [expiresAt, setExpiresAt] = useState('');
  const [reason, setReason] = useState('role_assignment');
  function assign(event: FormEvent) {
    event.preventDefault();
    void action.run(
      'POST',
      'role-assignments',
      {
        userId,
        roleId,
        expiresAt: expiresAt ? new Date(expiresAt).toISOString() : null,
        reason,
      },
      'Role assigned.',
      assignments.reload,
    );
  }
  return (
    <div className="access-stack">
      <Card title="Assign a role">
        <p>
          Assignments take effect without another login. Select an active member
          and a role.
        </p>
        <DataState
          loading={members.loading || roles.loading}
          error={members.error || roles.error}
        />
        <form className="access-form" onSubmit={assign}>
          <label>
            Member
            <select
              value={userId}
              onChange={(e) => setUserId(e.target.value)}
              required
            >
              <option value="">Choose a member</option>
              {members.data
                ?.filter((m) => m.status === 'active')
                .map((m) => (
                  <option value={m.userId} key={m.userId}>
                    {m.email}
                  </option>
                ))}
            </select>
          </label>
          <label>
            Role
            <select
              value={roleId}
              onChange={(e) => setRoleId(e.target.value)}
              required
            >
              <option value="">Choose a role</option>
              {roles.data
                ?.filter((r) => r.name !== 'owner')
                .map((r) => (
                  <option value={r.id} key={r.id}>
                    {r.name}
                  </option>
                ))}
            </select>
          </label>
          <label>
            Expiry (optional)
            <input
              type="datetime-local"
              value={expiresAt}
              onChange={(e) => setExpiresAt(e.target.value)}
            />
          </label>
          <ReasonField value={reason} onChange={setReason} />
          <button type="submit" disabled={action.busy}>
            Assign role
          </button>
        </form>
        <Feedback action={action} />
      </Card>
      <Card title="Active assignments">
        <DataState
          loading={assignments.loading}
          error={assignments.error}
          stepUp={assignments.stepUp}
        />
        {assignments.data?.length === 0 && <p>No active assignments.</p>}
        {assignments.data?.map((item) => (
          <div className="access-row" key={item.id}>
            <span>
              <strong>{item.roleName}</strong> ·{' '}
              {members.data?.find((m) => m.userId === item.userId)?.email ??
                'Member'}
              {item.expiresAt && (
                <> · Expires {new Date(item.expiresAt).toLocaleString()}</>
              )}
            </span>
            <button
              disabled={action.busy}
              onClick={() => {
                if (window.confirm('Revoke this role assignment?'))
                  void action.run(
                    'DELETE',
                    `role-assignments/${item.id}`,
                    { reason },
                    'Assignment revoked.',
                    assignments.reload,
                  );
              }}
            >
              Revoke
            </button>
          </div>
        ))}
      </Card>
    </div>
  );
}

function Apps({ slug }: { slug: string }) {
  const { data, loading, error } = useTenantData<
    { id: string; name: string }[]
  >(slug, 'apps');
  return (
    <Card title="Enabled apps">
      <DataState loading={loading} error={error} />
      <ul>
        {data?.map((app) => (
          <li key={app.id}>{app.name}</li>
        ))}
      </ul>
    </Card>
  );
}

function AuditLog({ slug }: { slug: string }) {
  const { data, loading, error } = useTenantData<Audit[]>(slug, 'audit-events');
  const action = useTenantAction(slug);
  const [filter, setFilter] = useState('');
  const [exportId, setExportId] = useState('');
  const exportStatus = useTenantData<AuditExport>(
    slug,
    `audit-events/exports/${exportId}`,
    Boolean(exportId),
  );
  return (
    <Card title="Audit log">
      <DataState loading={loading} error={error} />
      <div className="access-filters">
        <label>
          Search recent events
          <input
            value={filter}
            onChange={(event) => setFilter(event.target.value)}
            placeholder="Action, actor, target, or reason"
          />
        </label>
        <button
          disabled={action.busy}
          onClick={() => {
            void action
              .run<{ exportId: string }>(
                'POST',
                'audit-events/exports',
                undefined,
                'Audit export requested.',
              )
              .then((result) => {
                if (result?.exportId) setExportId(result.exportId);
              });
          }}
        >
          Request export
        </button>
      </div>
      <Feedback action={action} />
      {exportId && (
        <div role="status">
          <p>
            Export {exportId}: {exportStatus.data?.status ?? 'checking status'}.
          </p>
          {exportStatus.data?.status === 'ready' ? (
            <a
              href={tenantApiPath(
                slug,
                `audit-events/exports/${exportId}/download`,
              )}
              download="audit-export.jsonl"
            >
              Download export
            </a>
          ) : exportStatus.data?.status === 'failed' ? (
            <p>Export could not be completed.</p>
          ) : (
            <button
              type="button"
              disabled={exportStatus.loading}
              onClick={() => void exportStatus.reload()}
            >
              Refresh export status
            </button>
          )}
        </div>
      )}
      {data?.length === 0 && <p>No audit entries yet.</p>}
      {data && data.length > 0 && (
        <div className="access-table-wrap">
          <table>
            <thead>
              <tr>
                <th>When</th>
                <th>Action</th>
                <th>Actor</th>
                <th>Target</th>
                <th>Reason</th>
              </tr>
            </thead>
            <tbody>
              {data
                .filter((item) =>
                  `${item.action} ${item.actorType} ${item.targetType ?? ''} ${item.reason ?? ''}`
                    .toLocaleLowerCase()
                    .includes(filter.toLocaleLowerCase()),
                )
                .map((item) => (
                  <tr key={item.id}>
                    <td>{new Date(item.occurredAt).toLocaleString()}</td>
                    <td>{item.action}</td>
                    <td>{item.actorType}</td>
                    <td>{item.targetType ?? '—'}</td>
                    <td>{item.reason ?? '—'}</td>
                  </tr>
                ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

function SupportGrants({ slug }: { slug: string }) {
  const { data, loading, error, reload } = useTenantData<SupportGrant[]>(
    slug,
    'support-grants',
  );
  const action = useTenantAction(slug);
  const [reason, setReason] = useState('support_review');
  return (
    <Card title="Support grants">
      <p>Review time-limited platform support access to this tenant.</p>
      <DataState loading={loading} error={error} />
      {data?.length === 0 && <p>No support grants.</p>}
      {data && data.length > 0 && (
        <>
          <ReasonField value={reason} onChange={setReason} />
          <div className="access-table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Staff</th>
                  <th>Role</th>
                  <th>Requested reason</th>
                  <th>Status</th>
                  <th>Expires</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {data.map((grant) => (
                  <tr key={grant.id}>
                    <td>{grant.staffEmail}</td>
                    <td>{grant.roleName}</td>
                    <td>{grant.reason}</td>
                    <td>{grant.status}</td>
                    <td>{new Date(grant.expiresAt).toLocaleString()}</td>
                    <td>
                      {grant.status === 'requested' && (
                        <button
                          disabled={action.busy || !reason}
                          onClick={() =>
                            void action.run(
                              'POST',
                              `support-grants/${grant.id}/approve`,
                              { reason },
                              'Support access approved.',
                              reload,
                            )
                          }
                        >
                          Approve
                        </button>
                      )}
                      {['requested', 'approved', 'active'].includes(
                        grant.status,
                      ) && (
                        <button
                          disabled={action.busy || !reason}
                          onClick={() =>
                            void action.run(
                              'POST',
                              `support-grants/${grant.id}/revoke`,
                              { reason },
                              'Support access revoked.',
                              reload,
                            )
                          }
                        >
                          Revoke
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
      <Feedback action={action} />
    </Card>
  );
}

function TenantSettings({
  slug,
  canManage,
}: {
  slug: string;
  canManage: boolean;
}) {
  const { data, loading, error, reload } = useTenantData<Settings>(
    slug,
    'settings',
  );
  const action = useTenantAction(slug);
  const [name, setName] = useState('');
  const [newSlug, setNewSlug] = useState('');
  const [reason, setReason] = useState('tenant_settings');
  function save(event: FormEvent) {
    event.preventDefault();
    void action
      .run<Settings>(
        'PATCH',
        'settings',
        { name: name || data?.name, slug: newSlug || data?.slug, reason },
        'Settings saved.',
        reload,
      )
      .then((result) => {
        if (result?.slug && result.slug !== slug)
          window.location.assign(
            `/t/${encodeURIComponent(result.slug)}/settings`,
          );
      });
  }
  return (
    <Card title="Tenant settings">
      <DataState loading={loading} error={error} />
      {data && (
        <>
          <dl>
            <dt>Current name</dt>
            <dd>{data.name}</dd>
            <dt>Slug</dt>
            <dd>{data.slug}</dd>
            <dt>Plan</dt>
            <dd>{data.plan}</dd>
          </dl>
          {canManage && (
            <form className="access-form" onSubmit={save}>
              <label>
                New display name
                <input
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  placeholder={data.name}
                  maxLength={120}
                />
              </label>
              <label>
                New slug
                <input
                  value={newSlug}
                  onChange={(e) => setNewSlug(e.target.value)}
                  placeholder={data.slug}
                  pattern="[a-z0-9][a-z0-9-]{1,38}[a-z0-9]"
                />
              </label>
              <ReasonField value={reason} onChange={setReason} />
              <button disabled={action.busy} type="submit">
                Save name
              </button>
            </form>
          )}
        </>
      )}
      <Feedback action={action} />
    </Card>
  );
}

function Ownership({
  slug,
  canTransfer,
}: {
  slug: string;
  canTransfer: boolean;
}) {
  const members = useTenantData<Member[]>(slug, 'members', canTransfer);
  const action = useTenantAction(slug);
  const [userId, setUserId] = useState('');
  const [reason, setReason] = useState('ownership_transfer');
  const [transferId, setTransferId] = useState('');
  const [newTransferId, setNewTransferId] = useState('');
  return (
    <div className="access-stack">
      {canTransfer && (
        <Card title="Nominate a new owner">
          <p>
            The nominated member must accept before ownership changes. MFA is
            required.
          </p>
          <DataState loading={members.loading} error={members.error} />
          <form
            className="access-form"
            onSubmit={(event) => {
              event.preventDefault();
              void action
                .run<{ transferId: string }>(
                  'POST',
                  'ownership-transfers',
                  { userId, reason },
                  'Nomination created.',
                )
                .then((result) => {
                  if (result?.transferId) setNewTransferId(result.transferId);
                });
            }}
          >
            <label>
              Member
              <select
                value={userId}
                onChange={(e) => setUserId(e.target.value)}
                required
              >
                <option value="">Choose a member</option>
                {members.data
                  ?.filter((m) => m.status === 'active')
                  .map((m) => (
                    <option value={m.userId} key={m.userId}>
                      {m.email}
                    </option>
                  ))}
              </select>
            </label>
            <ReasonField value={reason} onChange={setReason} />
            <button disabled={action.busy} type="submit">
              Nominate
            </button>
          </form>
          {newTransferId && (
            <p>
              Give the nominee this transfer ID through a private channel:{' '}
              <code>{newTransferId}</code>
            </p>
          )}
          <Feedback action={action} />
        </Card>
      )}
      <Card title="Accept a nomination">
        <p>Enter the transfer ID supplied to you by the current owner.</p>
        <form
          className="access-form"
          onSubmit={(event) => {
            event.preventDefault();
            void action.run(
              'POST',
              `ownership-transfers/${transferId}/accept`,
              undefined,
              'Ownership transferred.',
            );
          }}
        >
          <label>
            Transfer ID
            <input
              value={transferId}
              onChange={(e) => setTransferId(e.target.value)}
              pattern="[0-9a-fA-F-]{36}"
              required
            />
          </label>
          <button disabled={action.busy} type="submit">
            Accept transfer
          </button>
        </form>
      </Card>
    </div>
  );
}

function Deletion({ slug }: { slug: string }) {
  const action = useTenantAction(slug);
  const [reason, setReason] = useState('tenant_deletion');
  const [confirmed, setConfirmed] = useState(false);
  return (
    <Card title="Request tenant deletion">
      <p>
        This starts the 14-day deletion grace period and immediately denies
        tenant access. MFA is required.
      </p>
      <form
        className="access-form"
        onSubmit={(event) => {
          event.preventDefault();
          if (
            confirmed &&
            window.confirm(
              'Start tenant deletion? Access will stop immediately.',
            )
          )
            void action.run(
              'POST',
              'deletion-requests',
              { reason },
              'Deletion requested.',
            );
        }}
      >
        <ReasonField value={reason} onChange={setReason} />
        <label className="access-check">
          <input
            type="checkbox"
            checked={confirmed}
            onChange={(e) => setConfirmed(e.target.checked)}
            required
          />
          I understand access will stop.
        </label>
        <button type="submit" disabled={action.busy || !confirmed}>
          Request deletion
        </button>
      </form>
      <Feedback action={action} />
    </Card>
  );
}

export function ConsoleSection({
  section,
  slug,
  tenantName,
  permissions,
}: {
  section: string;
  slug: string;
  tenantName: string;
  permissions: readonly string[];
}) {
  return (
    <>
      <p className="access-eyebrow">{tenantName} · Access</p>
      {section === 'my-access' && <MyAccess slug={slug} />}
      {section === 'members' && (
        <Members slug={slug} permissions={permissions} />
      )}
      {section === 'invitations' && <Invitations slug={slug} />}
      {section === 'roles' && (
        <Roles
          slug={slug}
          canManage={permissions.includes('access.role.manage')}
        />
      )}
      {section === 'assignments' && <Assignments slug={slug} />}
      {section === 'apps' && <Apps slug={slug} />}
      {section === 'audit' && <AuditLog slug={slug} />}
      {section === 'support-grants' && <SupportGrants slug={slug} />}
      {section === 'settings' && (
        <TenantSettings
          slug={slug}
          canManage={permissions.includes('access.settings.manage')}
        />
      )}
      {section === 'ownership' && (
        <Ownership
          slug={slug}
          canTransfer={permissions.includes('access.tenant.transfer')}
        />
      )}
      {section === 'deletion' && <Deletion slug={slug} />}
    </>
  );
}
