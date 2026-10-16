import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import prettier from 'prettier';
import { parse } from 'yaml';

const platformRoles = [
  'platform-admin',
  'platform-operator',
  'platform-support',
  'platform-auditor',
  'platform-security',
];

const staffManifest = parse(
  readFileSync(new URL('./platform-staff.yaml', import.meta.url), 'utf8'),
);
if (!staffManifest || !Array.isArray(staffManifest.staff))
  throw new Error('Invalid platform staff manifest');
const fixtureNames = new Map([
  ['penny.platform', ['Penny', 'Platform']],
  ['sam.support', ['Sam', 'Support']],
  ['sec.platform', ['Sec', 'Platform']],
]);
const seenStaff = new Set();
for (const entry of staffManifest.staff) {
  if (
    !entry ||
    typeof entry.username !== 'string' ||
    !fixtureNames.has(entry.username) ||
    seenStaff.has(entry.username) ||
    !Array.isArray(entry.roles) ||
    entry.roles.length === 0 ||
    new Set(entry.roles).size !== entry.roles.length ||
    !entry.roles.every((role) => platformRoles.includes(role))
  )
    throw new Error('Invalid local platform staff assignment');
  seenStaff.add(entry.username);
}

const flowIds = {
  browser: '0d11e28a-5d25-4ab4-9107-af5fb4bc5040',
  levels: '6af957ce-6d30-44f4-b3cb-428a202442a3',
  level1: '7b5c2d4e-c0d6-4121-879e-197f4530a6ac',
  level2: '2a7cb8a9-043d-4b07-89ac-ae51d7386918',
};

const execution = (priority, requirement, options) => ({
  priority,
  requirement,
  ...options,
});

const authFlow = (id, alias, topLevel, authenticationExecutions) => ({
  id,
  alias,
  description: 'Excellore password and fresh OTP authentication levels',
  providerId: 'basic-flow',
  topLevel,
  builtIn: false,
  authenticationExecutions,
});

const clientRoleMapper = (clientId) => ({
  name: `${clientId}-roles`,
  protocol: 'openid-connect',
  protocolMapper: 'oidc-usermodel-client-role-mapper',
  config: {
    'usermodel.clientRoleMapping.clientId': clientId,
    'claim.name': 'roles',
    'jsonType.label': 'String',
    multivalued: 'true',
    'access.token.claim': 'true',
    'id.token.claim': 'false',
    'userinfo.token.claim': 'false',
  },
});

const audienceMapper = (clientId) => ({
  name: `${clientId}-audience`,
  protocol: 'openid-connect',
  protocolMapper: 'oidc-audience-mapper',
  config: {
    'included.custom.audience': clientId,
    'access.token.claim': 'true',
    'id.token.claim': 'false',
  },
});

const scope = (name, mappers) => ({
  name,
  protocol: 'openid-connect',
  attributes: {
    'include.in.token.scope': 'true',
    'display.on.consent.screen': 'false',
  },
  protocolMappers: mappers,
});

const userPropertyMapper = (property, claim, jsonType = 'String') => ({
  name: claim,
  protocol: 'openid-connect',
  protocolMapper: 'oidc-usermodel-property-mapper',
  config: {
    'user.attribute': property,
    'claim.name': claim,
    'jsonType.label': jsonType,
    'access.token.claim': 'true',
    'id.token.claim': 'true',
    'userinfo.token.claim': 'true',
  },
});

const baseClient = (clientId, options = {}) => ({
  clientId,
  enabled: true,
  protocol: 'openid-connect',
  publicClient: false,
  consentRequired: false,
  standardFlowEnabled: false,
  implicitFlowEnabled: false,
  directAccessGrantsEnabled: false,
  serviceAccountsEnabled: false,
  fullScopeAllowed: false,
  defaultClientScopes: [
    'basic',
    'profile',
    'email',
    'acr',
    ...(options.scope ? [options.scope] : []),
  ],
  optionalClientScopes: [],
  ...options,
});

const bff = (app, origin, apiScope) =>
  baseClient(`excellore-${app}`, {
    scope: undefined,
    secret: `\${${app.toUpperCase()}_BFF_SECRET}`,
    standardFlowEnabled: true,
    redirectUris: [
      `\${${origin}}/auth/callback`,
      ...(app === 'access' ? [`\${${origin}}/invitations/accept`] : []),
    ],
    webOrigins: [],
    defaultClientScopes: [
      'basic',
      'profile',
      'email',
      'acr',
      ...(apiScope ? [apiScope] : []),
    ],
    protocolMappers:
      app === 'access'
        ? [
            {
              name: 'empty-platform-roles',
              protocol: 'openid-connect',
              protocolMapper: 'oidc-hardcoded-claim-mapper',
              config: {
                'claim.name': 'platform_roles',
                'claim.value': '[]',
                'jsonType.label': 'JSON',
                'access.token.claim': 'true',
                'id.token.claim': 'false',
                'userinfo.token.claim': 'false',
              },
            },
          ]
        : [],
    attributes: {
      'pkce.code.challenge.method': 'S256',
      'default.acr.values': '2',
      'post.logout.redirect.uris': `\${${origin}}/`,
      'backchannel.logout.url': `\${${origin}}/auth/backchannel-logout`,
      'backchannel.logout.session.required': 'true',
    },
  });

const machine = (clientId, secretKey, apiScope) =>
  baseClient(clientId, {
    secret: `\${${secretKey}}`,
    serviceAccountsEnabled: true,
    defaultClientScopes: ['basic', 'acr', ...(apiScope ? [apiScope] : [])],
  });

const devUser = (username, firstName, lastName, realmRoles = []) => ({
  username,
  firstName,
  lastName,
  email: `${username}@example.test`,
  emailVerified: true,
  enabled: true,
  credentials: [
    { type: 'password', value: '${DEV_USER_PASSWORD}', temporary: false },
    {
      type: 'otp',
      secretData: '{"value":"${DEV_OTP_SECRET}"}',
      credentialData:
        '{"digits":6,"counter":0,"period":30,"algorithm":"HmacSHA1","subType":"totp"}',
    },
  ],
  realmRoles,
});

const serviceUser = (clientId, clientRoles) => ({
  username: `service-account-${clientId}`,
  enabled: true,
  serviceAccountClientId: clientId,
  clientRoles,
});

const realm = {
  realm: 'excellore',
  enabled: true,
  displayName: 'Excellore',
  loginTheme: 'excellore',
  registrationAllowed: false,
  resetPasswordAllowed: true,
  verifyEmail: true,
  loginWithEmailAllowed: true,
  duplicateEmailsAllowed: false,
  editUsernameAllowed: false,
  accessTokenLifespan: 300,
  ssoSessionIdleTimeout: 1800,
  ssoSessionMaxLifespan: 36000,
  revokeRefreshToken: true,
  refreshTokenMaxReuse: 0,
  bruteForceProtected: true,
  failureFactor: 5,
  waitIncrementSeconds: 60,
  maxFailureWaitSeconds: 900,
  passwordPolicy:
    'length(12) and notUsername(undefined) and notEmail(undefined) and passwordHistory(5) and passwordBlacklist(excellore-common.txt)',
  requiredActions: [
    {
      alias: 'VERIFY_EMAIL',
      name: 'Verify Email',
      providerId: 'VERIFY_EMAIL',
      enabled: true,
      defaultAction: false,
    },
    {
      alias: 'UPDATE_PASSWORD',
      name: 'Update Password',
      providerId: 'UPDATE_PASSWORD',
      enabled: true,
      defaultAction: false,
    },
    {
      alias: 'CONFIGURE_TOTP',
      name: 'Configure OTP',
      providerId: 'CONFIGURE_TOTP',
      enabled: true,
      defaultAction: false,
    },
  ],
  otpPolicyType: 'totp',
  otpPolicyAlgorithm: 'HmacSHA1',
  otpPolicyDigits: 6,
  otpPolicyPeriod: 30,
  otpPolicyLookAheadWindow: 1,
  eventsEnabled: true,
  eventsListeners: [],
  adminEventsEnabled: true,
  adminEventsDetailsEnabled: false,
  browserFlow: 'excellore-browser',
  authenticatorConfig: [
    {
      alias: 'excellore-loa-1',
      config: { 'loa-condition-level': '1', 'loa-max-age': '36000' },
    },
    {
      alias: 'excellore-loa-2',
      config: { 'loa-condition-level': '2', 'loa-max-age': '0' },
    },
  ],
  authenticationFlows: [
    authFlow(flowIds.browser, 'excellore-browser', true, [
      execution(10, 'ALTERNATIVE', {
        authenticator: 'auth-cookie',
      }),
      execution(20, 'ALTERNATIVE', {
        authenticatorFlow: true,
        flowAlias: 'excellore-levels',
      }),
    ]),
    authFlow(flowIds.levels, 'excellore-levels', false, [
      execution(10, 'CONDITIONAL', {
        authenticatorFlow: true,
        flowAlias: 'excellore-level-1',
      }),
      execution(20, 'CONDITIONAL', {
        authenticatorFlow: true,
        flowAlias: 'excellore-level-2',
      }),
    ]),
    authFlow(flowIds.level1, 'excellore-level-1', false, [
      execution(10, 'REQUIRED', {
        authenticator: 'conditional-level-of-authentication',
        authenticatorConfig: 'excellore-loa-1',
      }),
      execution(20, 'REQUIRED', {
        authenticator: 'auth-username-password-form',
      }),
    ]),
    authFlow(flowIds.level2, 'excellore-level-2', false, [
      execution(10, 'REQUIRED', {
        authenticator: 'conditional-level-of-authentication',
        authenticatorConfig: 'excellore-loa-2',
      }),
      execution(20, 'REQUIRED', {
        authenticator: 'auth-otp-form',
      }),
    ]),
  ],
  roles: {
    realm: platformRoles.map((name) => ({ name })),
    client: {
      'api-access': [
        'access.authz.resolve',
        'access.catalog.sync',
        'access.worker',
      ].map((name) => ({ name })),
      'api-profile': [{ name: 'profile.worker' }],
    },
  },
  scopeMappings: [{ client: 'excellore-access', roles: platformRoles }],
  clientScopeMappings: {
    'api-access': [
      { client: 'worker-access', roles: ['access.worker'] },
      { client: 'svc-shell-authz', roles: ['access.authz.resolve'] },
      { client: 'svc-profile-authz', roles: ['access.authz.resolve'] },
      { client: 'svc-access-authz', roles: ['access.authz.resolve'] },
      { client: 'svc-catalog-sync', roles: ['access.catalog.sync'] },
    ],
    'api-profile': [{ client: 'worker-profile', roles: ['profile.worker'] }],
    'realm-management': [
      { client: 'worker-access', roles: ['view-users'] },
      { client: 'worker-profile', roles: ['view-users'] },
      {
        client: 'svc-access-admin',
        roles: ['query-users', 'view-users', 'manage-users'],
      },
      {
        client: 'svc-profile-identity',
        roles: ['view-users', 'manage-users'],
      },
    ],
  },
  clientScopes: [
    {
      name: 'basic',
      protocol: 'openid-connect',
      attributes: {
        'include.in.token.scope': 'false',
        'display.on.consent.screen': 'false',
      },
      protocolMappers: [
        {
          name: 'sub',
          protocol: 'openid-connect',
          protocolMapper: 'oidc-sub-mapper',
          config: {
            'introspection.token.claim': 'true',
            'access.token.claim': 'true',
          },
        },
        {
          name: 'auth_time',
          protocol: 'openid-connect',
          protocolMapper: 'oidc-usersessionmodel-note-mapper',
          config: {
            'user.session.note': 'AUTH_TIME',
            'id.token.claim': 'true',
            'introspection.token.claim': 'true',
            'access.token.claim': 'true',
            'claim.name': 'auth_time',
            'jsonType.label': 'long',
          },
        },
      ],
    },
    scope('profile', [
      userPropertyMapper('username', 'preferred_username'),
      userPropertyMapper('firstName', 'given_name'),
      userPropertyMapper('lastName', 'family_name'),
      {
        name: 'full-name',
        protocol: 'openid-connect',
        protocolMapper: 'oidc-full-name-mapper',
        config: {
          'access.token.claim': 'true',
          'id.token.claim': 'true',
          'userinfo.token.claim': 'true',
        },
      },
    ]),
    scope('email', [
      userPropertyMapper('email', 'email'),
      userPropertyMapper('emailVerified', 'email_verified', 'boolean'),
    ]),
    scope('acr', [
      {
        name: 'acr-loa',
        protocol: 'openid-connect',
        protocolMapper: 'oidc-acr-mapper',
        config: { 'access.token.claim': 'true', 'id.token.claim': 'true' },
      },
    ]),
    scope('api-profile', [
      audienceMapper('api-profile'),
      clientRoleMapper('api-profile'),
    ]),
    scope('api-access', [
      audienceMapper('api-access'),
      clientRoleMapper('api-access'),
      {
        name: 'platform-roles',
        protocol: 'openid-connect',
        protocolMapper: 'oidc-usermodel-realm-role-mapper',
        config: {
          'claim.name': 'platform_roles',
          'jsonType.label': 'String',
          multivalued: 'true',
          'access.token.claim': 'true',
          'id.token.claim': 'false',
          'userinfo.token.claim': 'false',
        },
      },
    ]),
  ],
  clients: [
    bff('shell', 'SHELL_ORIGIN'),
    bff('profile', 'PROFILE_ORIGIN', 'api-profile'),
    bff('access', 'ACCESS_ORIGIN', 'api-access'),
    baseClient('api-profile', { bearerOnly: true }),
    baseClient('api-access', { bearerOnly: true }),
    machine('worker-profile', 'WORKER_PROFILE_SECRET', 'api-profile'),
    machine('worker-access', 'WORKER_ACCESS_SECRET', 'api-access'),
    machine('svc-shell-authz', 'SVC_SHELL_AUTHZ_SECRET', 'api-access'),
    machine('svc-profile-authz', 'SVC_PROFILE_AUTHZ_SECRET', 'api-access'),
    machine('svc-access-authz', 'SVC_ACCESS_AUTHZ_SECRET', 'api-access'),
    machine('svc-catalog-sync', 'SVC_CATALOG_SYNC_SECRET', 'api-access'),
    machine('svc-access-admin', 'SVC_ACCESS_ADMIN_SECRET'),
    machine('svc-profile-identity', 'SVC_PROFILE_IDENTITY_SECRET'),
  ],
  users: [
    devUser('olivia.owner', 'Olivia', 'Owner'),
    devUser('adam.admin', 'Adam', 'Admin'),
    devUser('mia.member', 'Mia', 'Member'),
    devUser('vera.viewer', 'Vera', 'Viewer'),
    devUser('gina.globex', 'Gina', 'Globex'),
    devUser('nora.nomember', 'Nora', 'NoMember'),
    ...staffManifest.staff.map(({ username, roles }) => {
      const [first, last] = fixtureNames.get(username);
      return devUser(username, first, last, roles);
    }),
    serviceUser('worker-profile', {
      'api-profile': ['profile.worker'],
      'realm-management': ['view-users'],
    }),
    serviceUser('worker-access', {
      'api-access': ['access.worker'],
      'realm-management': ['view-users'],
    }),
    serviceUser('svc-shell-authz', { 'api-access': ['access.authz.resolve'] }),
    serviceUser('svc-profile-authz', {
      'api-access': ['access.authz.resolve'],
    }),
    serviceUser('svc-access-authz', {
      'api-access': ['access.authz.resolve'],
    }),
    serviceUser('svc-catalog-sync', { 'api-access': ['access.catalog.sync'] }),
    serviceUser('svc-access-admin', {
      'realm-management': ['query-users', 'view-users', 'manage-users'],
    }),
    serviceUser('svc-profile-identity', {
      'realm-management': ['view-users', 'manage-users'],
    }),
  ],
};

const output = fileURLToPath(new URL('./realm-dev.json', import.meta.url));
writeFileSync(
  output,
  await prettier.format(JSON.stringify(realm), {
    ...(await prettier.resolveConfig(output)),
    parser: 'json',
  }),
);
