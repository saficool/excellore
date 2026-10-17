import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const env = Object.fromEntries(
  readFileSync(new URL('../.env', import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => {
      const separator = line.indexOf('=');
      return [line.slice(0, separator), line.slice(separator + 1)];
    }),
);

const browserProtocol = process.env.CI === 'true' ? 'http:' : 'https:';
for (const name of [
  'SHELL_ORIGIN',
  'PROFILE_ORIGIN',
  'ACCESS_ORIGIN',
  'PLATFORM_ORIGIN',
]) {
  const origin = new URL(env[name]);
  assert.equal(origin.protocol, browserProtocol);
  assert.equal(origin.hostname.endsWith('.localhost'), true);
  assert.equal(origin.pathname, '/');
  assert.equal(origin.search, '');
  assert.equal(origin.hash, '');
}
const base = env.KEYCLOAK_ADMIN_ORIGIN;
const login = await fetch(
  `${base}/realms/master/protocol/openid-connect/token`,
  {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'password',
      client_id: 'admin-cli',
      username: 'admin',
      password: env.KEYCLOAK_ADMIN_PASSWORD,
    }),
  },
);
assert.equal(login.status, 200, 'local Keycloak admin login');
const { access_token: token } = await login.json();
const endpoint = `${base}/admin/realms/excellore/clients`;
const clientsResponse = await fetch(endpoint, {
  headers: { authorization: `Bearer ${token}` },
});
assert.equal(clientsResponse.status, 200, 'local Keycloak client list');
let clients = await clientsResponse.json();
const realmDefinition = JSON.parse(
  readFileSync(
    new URL('../.local/keycloak/realm-dev.json', import.meta.url),
    'utf8',
  ),
);
if (!clients.some((candidate) => candidate.clientId === 'excellore-platform')) {
  const definition = realmDefinition.clients.find(
    (candidate) => candidate.clientId === 'excellore-platform',
  );
  assert.ok(definition, 'declarative Platform BFF client');
  const created = await fetch(endpoint, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify(definition),
  });
  const creationError = created.status === 201 ? '' : await created.text();
  assert.equal(
    created.status,
    201,
    `local Platform BFF client creation: ${creationError}`,
  );
  const refreshed = await fetch(endpoint, {
    headers: { authorization: `Bearer ${token}` },
  });
  assert.equal(refreshed.status, 200, 'local BFF client refresh');
  clients = await refreshed.json();
}
for (const app of ['shell', 'profile', 'access', 'platform']) {
  const client = clients.find(
    (candidate) => candidate.clientId === `excellore-${app}`,
  );
  assert.ok(client?.id, `local ${app} BFF client`);
  const origin = env[`${app.toUpperCase()}_ORIGIN`];
  const expected = [
    `${origin}/auth/callback`,
    ...(app === 'access' ? [`${origin}/invitations/accept`] : []),
  ];
  const attributes = {
    ...client.attributes,
    'post.logout.redirect.uris': `${origin}/`,
    'backchannel.logout.url': `${origin}/auth/backchannel-logout`,
  };
  if (
    JSON.stringify(client.redirectUris) !== JSON.stringify(expected) ||
    client.attributes?.['post.logout.redirect.uris'] !==
      attributes['post.logout.redirect.uris'] ||
    client.attributes?.['backchannel.logout.url'] !==
      attributes['backchannel.logout.url']
  ) {
    const update = await fetch(`${endpoint}/${client.id}`, {
      method: 'PUT',
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify({ ...client, redirectUris: expected, attributes }),
    });
    assert.equal(update.status, 204, `local ${app} redirect update`);
  }
}

async function mappersAt(mapperEndpoint, label) {
  const response = await fetch(mapperEndpoint, {
    headers: { authorization: `Bearer ${token}` },
  });
  assert.equal(response.status, 200, label);
  return response.json();
}

async function removeMapper(mapperEndpoint, mapper, label) {
  const response = await fetch(`${mapperEndpoint}/${mapper.id}`, {
    method: 'DELETE',
    headers: { authorization: `Bearer ${token}` },
  });
  assert.equal(response.status, 204, label);
}

const clientScopesResponse = await fetch(
  `${base}/admin/realms/excellore/client-scopes`,
  { headers: { authorization: `Bearer ${token}` } },
);
assert.equal(clientScopesResponse.status, 200, 'local client scopes');
const apiAccessScope = (await clientScopesResponse.json()).find(
  (scope) => scope.name === 'api-access',
);
assert.ok(apiAccessScope?.id, 'local api-access scope');
const apiAccessMapperEndpoint = `${base}/admin/realms/excellore/client-scopes/${apiAccessScope.id}/protocol-mappers/models`;
for (const mapper of await mappersAt(
  apiAccessMapperEndpoint,
  'local api-access mappers',
)) {
  if (mapper.name === 'platform-roles')
    await removeMapper(
      apiAccessMapperEndpoint,
      mapper,
      'remove shared platform role mapper',
    );
}

for (const app of ['access', 'platform']) {
  const client = clients.find(
    (candidate) => candidate.clientId === `excellore-${app}`,
  );
  const mapperEndpoint = `${endpoint}/${client.id}/protocol-mappers/models`;
  const mappers = await mappersAt(mapperEndpoint, `local ${app} mappers`);
  for (const mapper of mappers) {
    if (
      mapper.name === 'empty-platform-roles' ||
      (app === 'access' && mapper.name === 'platform-roles')
    )
      await removeMapper(
        mapperEndpoint,
        mapper,
        `remove stale ${app} platform mapper`,
      );
  }
  if (
    app === 'platform' &&
    !mappers.some((mapper) => mapper.name === 'platform-roles')
  ) {
    const definition = realmDefinition.clients.find(
      (candidate) => candidate.clientId === 'excellore-platform',
    );
    const desiredMapper = definition.protocolMappers.find(
      (mapper) => mapper.name === 'platform-roles',
    );
    assert.ok(desiredMapper, 'declarative Platform role mapper');
    const created = await fetch(mapperEndpoint, {
      method: 'POST',
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify(desiredMapper),
    });
    assert.equal(created.status, 201, 'create Platform role mapper');
  }
}

const platformRoleNames = [
  'platform-admin',
  'platform-operator',
  'platform-support',
  'platform-auditor',
  'platform-security',
];
const realmRolesResponse = await fetch(`${base}/admin/realms/excellore/roles`, {
  headers: { authorization: `Bearer ${token}` },
});
assert.equal(realmRolesResponse.status, 200, 'local realm roles');
const platformRoles = (await realmRolesResponse.json()).filter((role) =>
  platformRoleNames.includes(role.name),
);
assert.equal(platformRoles.length, platformRoleNames.length, 'platform roles');
const accessBff = clients.find(
  (candidate) => candidate.clientId === 'excellore-access',
);
const platformBff = clients.find(
  (candidate) => candidate.clientId === 'excellore-platform',
);
assert.ok(accessBff?.id && platformBff?.id, 'local isolated BFF clients');
const accessRealmMappings = await fetch(
  `${endpoint}/${accessBff.id}/scope-mappings/realm`,
  { headers: { authorization: `Bearer ${token}` } },
);
assert.equal(accessRealmMappings.status, 200, 'Access BFF realm scope');
const staleAccessRoles = (await accessRealmMappings.json()).filter((role) =>
  platformRoleNames.includes(role.name),
);
if (staleAccessRoles.length) {
  const removed = await fetch(
    `${endpoint}/${accessBff.id}/scope-mappings/realm`,
    {
      method: 'DELETE',
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify(staleAccessRoles),
    },
  );
  assert.equal(removed.status, 204, 'remove platform scope from Access BFF');
}
const platformRealmMappings = await fetch(
  `${endpoint}/${platformBff.id}/scope-mappings/realm`,
  { headers: { authorization: `Bearer ${token}` } },
);
assert.equal(platformRealmMappings.status, 200, 'Platform BFF realm scope');
const mappedNames = new Set(
  (await platformRealmMappings.json()).map((role) => role.name),
);
const missingPlatformRoles = platformRoles.filter(
  (role) => !mappedNames.has(role.name),
);
if (missingPlatformRoles.length) {
  const mapped = await fetch(
    `${endpoint}/${platformBff.id}/scope-mappings/realm`,
    {
      method: 'POST',
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify(missingPlatformRoles),
    },
  );
  assert.equal(mapped.status, 204, 'Platform BFF role scope');
}
console.log('Local BFF redirects and role scopes match the generated realm.');

// Realm import does not modify an already imported development realm. Reconcile
// the Access BFF's dedicated machine identity without resetting local users.
let accessMachine = (
  await (
    await fetch(endpoint, {
      headers: { authorization: `Bearer ${token}` },
    })
  ).json()
).find((candidate) => candidate.clientId === 'svc-access-authz');
if (!accessMachine) {
  const created = await fetch(endpoint, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({
      clientId: 'svc-access-authz',
      enabled: true,
      protocol: 'openid-connect',
      publicClient: false,
      standardFlowEnabled: false,
      implicitFlowEnabled: false,
      directAccessGrantsEnabled: false,
      serviceAccountsEnabled: true,
      fullScopeAllowed: false,
      defaultClientScopes: ['basic', 'acr', 'api-access'],
      optionalClientScopes: [],
      secret: env.SVC_ACCESS_AUTHZ_SECRET,
    }),
  });
  assert.equal(created.status, 201, 'local Access authority client creation');
  accessMachine = (
    await (
      await fetch(endpoint, {
        headers: { authorization: `Bearer ${token}` },
      })
    ).json()
  ).find((candidate) => candidate.clientId === 'svc-access-authz');
}
assert.ok(accessMachine?.id, 'local Access authority client');
const apiClient = (
  await (
    await fetch(endpoint, {
      headers: { authorization: `Bearer ${token}` },
    })
  ).json()
).find((candidate) => candidate.clientId === 'api-access');
assert.ok(apiClient?.id, 'local Access API client');
const roleResponse = await fetch(
  `${endpoint}/${apiClient.id}/roles/access.authz.resolve`,
  {
    headers: { authorization: `Bearer ${token}` },
  },
);
assert.equal(roleResponse.status, 200, 'Access resolve role');
const role = await roleResponse.json();
const scoped = await fetch(
  `${endpoint}/${accessMachine.id}/scope-mappings/clients/${apiClient.id}`,
  {
    headers: { authorization: `Bearer ${token}` },
  },
);
assert.equal(scoped.status, 200, 'Access authority scope');
if (!(await scoped.json()).some((item) => item.name === role.name)) {
  const mapped = await fetch(
    `${endpoint}/${accessMachine.id}/scope-mappings/clients/${apiClient.id}`,
    {
      method: 'POST',
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify([role]),
    },
  );
  assert.equal(mapped.status, 204, 'Access authority role scope');
}
const serviceUserResponse = await fetch(
  `${endpoint}/${accessMachine.id}/service-account-user`,
  {
    headers: { authorization: `Bearer ${token}` },
  },
);
assert.equal(
  serviceUserResponse.status,
  200,
  'Access authority service account',
);
const serviceUser = await serviceUserResponse.json();
const assigned = await fetch(
  `${base}/admin/realms/excellore/users/${serviceUser.id}/role-mappings/clients/${apiClient.id}`,
  {
    headers: { authorization: `Bearer ${token}` },
  },
);
assert.equal(assigned.status, 200, 'Access authority role mapping');
if (!(await assigned.json()).some((item) => item.name === role.name)) {
  const mapped = await fetch(
    `${base}/admin/realms/excellore/users/${serviceUser.id}/role-mappings/clients/${apiClient.id}`,
    {
      method: 'POST',
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify([role]),
    },
  );
  assert.equal(mapped.status, 204, 'Access authority service role');
}
console.log('Local Access BFF authority client is configured.');

// Add the Notifications recipient resolver identity and its single Access role
// without resetting local users or their OTP enrollments.
let notificationMachine = (
  await (
    await fetch(endpoint, { headers: { authorization: `Bearer ${token}` } })
  ).json()
).find((candidate) => candidate.clientId === 'svc-notifications-authz');
if (!notificationMachine) {
  const definition = realmDefinition.clients.find(
    (candidate) => candidate.clientId === 'svc-notifications-authz',
  );
  assert.ok(definition, 'declarative Notifications resolver client');
  const created = await fetch(endpoint, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify(definition),
  });
  assert.equal(created.status, 201, 'local Notifications resolver client');
  notificationMachine = (
    await (
      await fetch(endpoint, { headers: { authorization: `Bearer ${token}` } })
    ).json()
  ).find((candidate) => candidate.clientId === 'svc-notifications-authz');
}
assert.ok(notificationMachine?.id, 'local Notifications resolver client');
const notificationRoleName = 'access.notification.contact.resolve';
let notificationRoleResponse = await fetch(
  `${endpoint}/${apiClient.id}/roles/${notificationRoleName}`,
  { headers: { authorization: `Bearer ${token}` } },
);
if (notificationRoleResponse.status === 404) {
  const created = await fetch(`${endpoint}/${apiClient.id}/roles`, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({ name: notificationRoleName }),
  });
  assert.equal(created.status, 201, 'create Notifications contact role');
  notificationRoleResponse = await fetch(
    `${endpoint}/${apiClient.id}/roles/${notificationRoleName}`,
    { headers: { authorization: `Bearer ${token}` } },
  );
}
assert.equal(
  notificationRoleResponse.status,
  200,
  'Notifications contact role',
);
const notificationRole = await notificationRoleResponse.json();
for (const mapping of [
  `${endpoint}/${notificationMachine.id}/scope-mappings/clients/${apiClient.id}`,
  `${base}/admin/realms/excellore/users/${
    (
      await (
        await fetch(
          `${endpoint}/${notificationMachine.id}/service-account-user`,
          {
            headers: { authorization: `Bearer ${token}` },
          },
        )
      ).json()
    ).id
  }/role-mappings/clients/${apiClient.id}`,
]) {
  const assignedResponse = await fetch(mapping, {
    headers: { authorization: `Bearer ${token}` },
  });
  assert.equal(assignedResponse.status, 200, 'Notifications role mapping');
  if (
    !(await assignedResponse.json()).some(
      (item) => item.name === notificationRoleName,
    )
  ) {
    const assigned = await fetch(mapping, {
      method: 'POST',
      headers: {
        authorization: `Bearer ${token}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify([notificationRole]),
    });
    assert.equal(assigned.status, 204, 'assign Notifications contact role');
  }
}

const realmEndpoint = `${base}/admin/realms/excellore`;
const realmResponse = await fetch(realmEndpoint, {
  headers: { authorization: `Bearer ${token}` },
});
assert.equal(realmResponse.status, 200, 'local realm configuration');
const currentRealm = await realmResponse.json();
if (
  JSON.stringify(currentRealm.smtpServer) !==
  JSON.stringify(realmDefinition.smtpServer)
) {
  const updated = await fetch(realmEndpoint, {
    method: 'PUT',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({
      ...currentRealm,
      smtpServer: realmDefinition.smtpServer,
    }),
  });
  assert.equal(updated.status, 204, 'local realm SMTP update');
}
console.log('Local Notifications resolver and Keycloak SMTP are configured.');

const scopesResponse = await fetch(
  `${base}/admin/realms/excellore/client-scopes`,
  {
    headers: { authorization: `Bearer ${token}` },
  },
);
assert.equal(scopesResponse.status, 200, 'local client scopes');
let basic = (await scopesResponse.json()).find(
  (scope) => scope.name === 'basic',
);
if (!basic) {
  const realmDefinition = JSON.parse(
    readFileSync(
      new URL('../infra/keycloak/realm-dev.json', import.meta.url),
      'utf8',
    ),
  );
  const definition = realmDefinition.clientScopes.find(
    (scope) => scope.name === 'basic',
  );
  assert.ok(definition, 'declarative basic scope');
  const created = await fetch(`${base}/admin/realms/excellore/client-scopes`, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify(definition),
  });
  assert.equal(created.status, 201, 'local basic scope creation');
  const refreshed = await fetch(
    `${base}/admin/realms/excellore/client-scopes`,
    {
      headers: { authorization: `Bearer ${token}` },
    },
  );
  assert.equal(refreshed.status, 200, 'local basic scope lookup');
  basic = (await refreshed.json()).find((scope) => scope.name === 'basic');
}
assert.ok(basic?.id, 'Keycloak basic subject scope');
for (const candidate of (
  await (
    await fetch(endpoint, {
      headers: { authorization: `Bearer ${token}` },
    })
  ).json()
).filter(
  (item) =>
    item.clientId.startsWith('excellore-') ||
    item.clientId.startsWith('svc-') ||
    item.clientId.startsWith('worker-'),
)) {
  const assignedResponse = await fetch(
    `${endpoint}/${candidate.id}/default-client-scopes`,
    {
      headers: { authorization: `Bearer ${token}` },
    },
  );
  assert.equal(assignedResponse.status, 200, 'local default client scopes');
  if (
    !(await assignedResponse.json()).some((scope) => scope.name === 'basic')
  ) {
    const added = await fetch(
      `${endpoint}/${candidate.id}/default-client-scopes/${basic.id}`,
      {
        method: 'PUT',
        headers: { authorization: `Bearer ${token}` },
      },
    );
    assert.equal(added.status, 204, 'local subject scope mapping');
  }
}
console.log('Local client access tokens include the standard subject scope.');

const authentication = `${base}/admin/realms/excellore/authentication`;
const executionResponse = await fetch(
  `${authentication}/flows/${encodeURIComponent('excellore-level-2')}/executions`,
  { headers: { authorization: `Bearer ${token}` } },
);
assert.equal(executionResponse.status, 200, 'local level 2 flow');
const condition = (await executionResponse.json()).find(
  (item) => item.providerId === 'conditional-level-of-authentication',
);
assert.ok(condition?.authenticationConfig, 'local level 2 condition');
const conditionEndpoint = `${authentication}/config/${condition.authenticationConfig}`;
const conditionResponse = await fetch(conditionEndpoint, {
  headers: { authorization: `Bearer ${token}` },
});
assert.equal(conditionResponse.status, 200, 'local level 2 configuration');
const conditionConfig = await conditionResponse.json();
if (conditionConfig.config?.['loa-max-age'] !== '36000') {
  const updated = await fetch(conditionEndpoint, {
    method: 'PUT',
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({
      ...conditionConfig,
      config: { ...conditionConfig.config, 'loa-max-age': '36000' },
    }),
  });
  assert.equal(updated.status, 204, 'local level 2 configuration update');
}
console.log('Local MFA SSO freshness matches the generated realm.');
