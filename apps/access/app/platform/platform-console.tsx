'use client';

import { useCallback, useEffect, useState } from 'react';
import { apiRequest, errorMessage } from '../../lib/browser-api';

type Tenant = {
  id: string;
  slug: string;
  name: string;
  status: string;
  plan: string;
  deletionDueAt: string | null;
};
type App = { id: string; name: string; core: boolean; status: string };
type Detail = { tenant: Tenant; apps: App[] };
type Permission = {
  key: string;
  appId: string;
  description: string;
  privileged: boolean;
};
type Audit = {
  id: string;
  occurredAt: string;
  action: string;
  targetId: string | null;
  reason: string | null;
};
type SupportRole = { id: string; name: string };
type SystemRole = { id: string; name: string; description: string | null };
type OwnGrant = {
  id: string;
  roleName: string;
  status: string;
  expiresAt: string;
};

const path = (part: string) => `/api/platform/${part}`;
const mutating = (roles: string[]) =>
  roles.includes('platform-operator') || roles.includes('platform-admin');
const suspending = (roles: string[]) =>
  mutating(roles) || roles.includes('platform-security');
const supporting = (roles: string[]) =>
  roles.includes('platform-support') || roles.includes('platform-admin');
const auditing = (roles: string[]) =>
  roles.includes('platform-auditor') || roles.includes('platform-admin');
const navigation = (roles: string[]): [string, string][] => [
  ['tenants', 'Tenants'],
  ['permissions', 'Permission catalog'],
  ...(auditing(roles) ? [['audit', 'Platform audit'] as [string, string]] : []),
  ...(supporting(roles)
    ? [['support', 'Support requests'] as [string, string]]
    : []),
];

export function PlatformConsole({ roles }: { roles: string[] }) {
  const [section, setSection] = useState('tenants');
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [selected, setSelected] = useState<Detail | null>(null);
  const [permissions, setPermissions] = useState<Permission[]>([]);
  const [systemRoles, setSystemRoles] = useState<SystemRole[]>([]);
  const [audit, setAudit] = useState<Audit[]>([]);
  const [supportRoles, setSupportRoles] = useState<SupportRole[]>([]);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [name, setName] = useState('');
  const [slug, setSlug] = useState('');
  const [plan, setPlan] = useState('starter');
  const [ownerEmail, setOwnerEmail] = useState('');
  const [editName, setEditName] = useState('');
  const [editSlug, setEditSlug] = useState('');
  const [editPlan, setEditPlan] = useState('');
  const [reason, setReason] = useState('operator-change');
  const [grantTenantId, setGrantTenantId] = useState('');
  const [grantRoleId, setGrantRoleId] = useState('');
  const [duration, setDuration] = useState(60);
  const [ownGrants, setOwnGrants] = useState<OwnGrant[]>([]);

  const loadTenants = useCallback(async () => {
    setTenants(await apiRequest<Tenant[]>(path('tenants')));
  }, []);
  const loadDetail = useCallback(async (id: string) => {
    setSelected(await apiRequest<Detail>(path(`tenants/${id}`)));
  }, []);

  useEffect(() => {
    void loadTenants().catch((error) => setMessage(errorMessage(error)));
  }, [loadTenants]);

  async function loadSection(next: string) {
    setSection(next);
    setMessage('');
    try {
      if (next === 'permissions') {
        setPermissions(await apiRequest<Permission[]>(path('permissions')));
        setSystemRoles(await apiRequest<SystemRole[]>(path('system-roles')));
      }
      if (next === 'audit')
        setAudit(await apiRequest<Audit[]>(path('audit-events')));
      if (next === 'support')
        setSupportRoles(await apiRequest<SupportRole[]>(path('support-roles')));
    } catch (error) {
      setMessage(errorMessage(error));
    }
  }

  async function run(
    action: () => Promise<unknown>,
    success: string,
    refreshSelected = true,
  ) {
    setBusy(true);
    setMessage('');
    try {
      await action();
      await loadTenants();
      if (selected && refreshSelected) await loadDetail(selected.tenant.id);
      setMessage(success);
    } catch (error) {
      setMessage(errorMessage(error));
    } finally {
      setBusy(false);
    }
  }

  async function createTenant(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await run(
      async () => {
        const created = await apiRequest<{ tenantId: string }>(
          path('tenants'),
          'POST',
          { name, slug, plan, ownerEmail, initialApps: [] },
        );
        await loadDetail(created.tenantId);
        setEditName(name);
        setEditSlug(slug);
        setEditPlan(plan);
      },
      'Tenant created. The owner invitation is pending delivery.',
      false,
    );
  }

  async function editTenant(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selected) return;
    await run(
      () =>
        apiRequest(path(`tenants/${selected.tenant.id}`), 'PATCH', {
          name: editName,
          slug: editSlug,
          plan: editPlan,
          reason,
        }),
      'Tenant settings updated.',
    );
  }

  function selectTenant(tenant: Tenant) {
    setEditName(tenant.name);
    setEditSlug(tenant.slug);
    setEditPlan(tenant.plan);
    void loadDetail(tenant.id).catch((error) =>
      setMessage(errorMessage(error)),
    );
  }

  async function selectGrantTenant(id: string) {
    setGrantTenantId(id);
    setOwnGrants([]);
    if (!id) return;
    try {
      setOwnGrants(
        await apiRequest<OwnGrant[]>(path(`tenants/${id}/support-requests`)),
      );
    } catch (error) {
      setMessage(errorMessage(error));
    }
  }

  const current = selected?.tenant;
  const canEdit = mutating(roles);
  const canSuspend = suspending(roles);
  return (
    <div className="access-layout">
      <nav className="access-sidebar" aria-label="Platform sections">
        <a href="/account">My account</a>
        {navigation(roles).map(([id, label]) => (
          <button
            key={id}
            type="button"
            onClick={() => void loadSection(id)}
            aria-current={section === id ? 'page' : undefined}
          >
            {label}
          </button>
        ))}
      </nav>
      <main className="access-main access-stack">
        <header>
          <p className="access-eyebrow">Excellore Access</p>
          <h1>Platform console</h1>
          <p>Role: {roles.join(', ')}</p>
        </header>
        {message && <p role="status">{message}</p>}
        {section === 'tenants' && (
          <>
            <section className="ex-card access-card">
              <h2>Tenants</h2>
              {tenants.length === 0 && <p>No tenants available.</p>}
              {tenants.map((tenant) => (
                <div className="access-row" key={tenant.id}>
                  <span>
                    {tenant.name} ({tenant.slug}) · {tenant.status} ·{' '}
                    {tenant.plan}
                  </span>
                  <button type="button" onClick={() => selectTenant(tenant)}>
                    Open
                  </button>
                </div>
              ))}
            </section>
            {canEdit && (
              <section className="ex-card access-card">
                <h2>Create tenant</h2>
                <form
                  className="access-form"
                  onSubmit={(event) => void createTenant(event)}
                >
                  <label>
                    Name
                    <input
                      required
                      maxLength={160}
                      value={name}
                      onChange={(event) => setName(event.target.value)}
                    />
                  </label>
                  <label>
                    Slug
                    <input
                      required
                      value={slug}
                      onChange={(event) => setSlug(event.target.value)}
                    />
                  </label>
                  <label>
                    Plan
                    <input
                      required
                      value={plan}
                      onChange={(event) => setPlan(event.target.value)}
                    />
                  </label>
                  <label>
                    Owner email
                    <input
                      type="email"
                      required
                      value={ownerEmail}
                      onChange={(event) => setOwnerEmail(event.target.value)}
                    />
                  </label>
                  <button disabled={busy}>Create tenant</button>
                </form>
              </section>
            )}
            {selected && (
              <section className="ex-card access-card">
                <h2>{current?.name}</h2>
                <p>
                  Status: {current?.status} · Plan: {current?.plan}
                </p>
                {current?.deletionDueAt && (
                  <p>
                    Deletion due:{' '}
                    {new Date(current.deletionDueAt).toLocaleString()}
                  </p>
                )}
                {canEdit && (
                  <form
                    className="access-form"
                    onSubmit={(event) => void editTenant(event)}
                  >
                    <label>
                      Name
                      <input
                        required
                        value={editName}
                        onChange={(event) => setEditName(event.target.value)}
                      />
                    </label>
                    <label>
                      Slug
                      <input
                        required
                        value={editSlug}
                        onChange={(event) => setEditSlug(event.target.value)}
                      />
                    </label>
                    <label>
                      Plan
                      <input
                        required
                        value={editPlan}
                        onChange={(event) => setEditPlan(event.target.value)}
                      />
                    </label>
                    <label>
                      Reason code
                      <input
                        required
                        value={reason}
                        onChange={(event) => setReason(event.target.value)}
                      />
                    </label>
                    <button disabled={busy}>Save tenant</button>
                  </form>
                )}
                {canSuspend && current?.status === 'active' && (
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() =>
                      void run(
                        () =>
                          apiRequest(
                            path(`tenants/${current.id}/suspend`),
                            'POST',
                            { reason },
                          ),
                        'Tenant suspended.',
                      )
                    }
                  >
                    Suspend tenant
                  </button>
                )}
                {canEdit && current?.status === 'suspended' && (
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() =>
                      void run(
                        () =>
                          apiRequest(
                            path(`tenants/${current.id}/reactivate`),
                            'POST',
                            { reason },
                          ),
                        'Tenant reactivated.',
                      )
                    }
                  >
                    Reactivate tenant
                  </button>
                )}
                {canEdit &&
                  ['active', 'suspended'].includes(current?.status ?? '') && (
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => {
                        if (
                          window.confirm(
                            'Request tenant deletion after the 14 day grace period?',
                          )
                        )
                          void run(
                            () =>
                              apiRequest(
                                path(
                                  `tenants/${current!.id}/deletion-requests`,
                                ),
                                'POST',
                                { reason },
                              ),
                            'Tenant deletion requested.',
                          );
                      }}
                    >
                      Request deletion
                    </button>
                  )}
                <h3>Apps</h3>
                {selected.apps.map((app) => (
                  <div className="access-row" key={app.id}>
                    <span>
                      {app.name} · {app.status}
                      {app.core ? ' · core' : ''}
                    </span>
                    {canEdit && !app.core && (
                      <button
                        type="button"
                        disabled={busy}
                        onClick={() =>
                          void run(
                            () =>
                              apiRequest(
                                path(
                                  `tenants/${selected.tenant.id}/apps/${app.id}`,
                                ),
                                'PUT',
                                { enabled: app.status !== 'enabled', reason },
                              ),
                            app.status === 'enabled'
                              ? 'App disabled.'
                              : 'App enabled.',
                          )
                        }
                      >
                        {app.status === 'enabled' ? 'Disable' : 'Enable'}
                      </button>
                    )}
                  </div>
                ))}
              </section>
            )}
          </>
        )}
        {section === 'permissions' && (
          <section className="ex-card access-card">
            <h2>Permission catalog</h2>
            <h3>System roles</h3>
            {systemRoles.map((role) => (
              <p key={role.id}>
                <strong>{role.name}</strong>
                {role.description ? ` · ${role.description}` : ''}
              </p>
            ))}
            <h3>Permissions</h3>
            {permissions.map((permission) => (
              <p key={permission.key}>
                <strong>{permission.key}</strong> · {permission.description}
                {permission.privileged ? ' · privileged' : ''}
              </p>
            ))}
          </section>
        )}
        {section === 'audit' && (
          <section className="ex-card access-card">
            <h2>Platform audit</h2>
            {audit.map((event) => (
              <p key={event.id}>
                {new Date(event.occurredAt).toLocaleString()} · {event.action}
                {event.targetId ? ` · ${event.targetId}` : ''}
                {event.reason ? ` · ${event.reason}` : ''}
              </p>
            ))}
          </section>
        )}
        {section === 'support' && (
          <section className="ex-card access-card">
            <h2>Request support access</h2>
            <p>
              Tenant owner approval is required. Access expires automatically.
            </p>
            <form
              className="access-form"
              onSubmit={(event) => {
                event.preventDefault();
                void run(async () => {
                  await apiRequest(path('support-requests'), 'POST', {
                    tenantId: grantTenantId,
                    roleId: grantRoleId,
                    durationMinutes: duration,
                    reason,
                  });
                  setOwnGrants(
                    await apiRequest<OwnGrant[]>(
                      path(`tenants/${grantTenantId}/support-requests`),
                    ),
                  );
                }, 'Support request sent to the tenant owner.');
              }}
            >
              <label>
                Tenant
                <select
                  required
                  value={grantTenantId}
                  onChange={(event) =>
                    void selectGrantTenant(event.target.value)
                  }
                >
                  <option value="">Choose tenant</option>
                  {tenants.map((tenant) => (
                    <option key={tenant.id} value={tenant.id}>
                      {tenant.name}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Read-only role
                <select
                  required
                  value={grantRoleId}
                  onChange={(event) => setGrantRoleId(event.target.value)}
                >
                  <option value="">Choose role</option>
                  {supportRoles.map((role) => (
                    <option key={role.id} value={role.id}>
                      {role.name}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Minutes
                <input
                  type="number"
                  min={1}
                  max={480}
                  value={duration}
                  onChange={(event) => setDuration(Number(event.target.value))}
                />
              </label>
              <label>
                Reason code
                <input
                  required
                  value={reason}
                  onChange={(event) => setReason(event.target.value)}
                />
              </label>
              <button disabled={busy}>Request access</button>
            </form>
            {ownGrants.map((grant) => (
              <p key={grant.id}>
                {grant.roleName} · {grant.status} · expires{' '}
                {new Date(grant.expiresAt).toLocaleString()}
              </p>
            ))}
          </section>
        )}
      </main>
    </div>
  );
}
