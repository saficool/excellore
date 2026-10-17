import { readFileSync, readdirSync, statSync } from 'node:fs';
import { resolve } from 'node:path';
import { parseAllDocuments } from 'yaml';

const args = process.argv.slice(2).filter((argument) => argument !== '--');
const input = resolve(
  args.find((argument) => argument !== '--deployment') ??
    '.local/production-manifests',
);
const deploymentMode = args.includes('--deployment');
const files = statSync(input).isDirectory()
  ? walk(input).filter((path) => /\.ya?ml$/u.test(path))
  : [input];
const resources = files.flatMap((path) =>
  parseAllDocuments(readFileSync(path, 'utf8'))
    .map((document) => document.toJSON())
    .filter(Boolean),
);

assert(resources.length > 0, 'rendered chart must contain resources');
assert(
  !resources.some((resource) => resource.kind === 'Secret'),
  'rendered chart must not contain plaintext Kubernetes Secrets',
);

const deployments = resources.filter(
  (resource) => resource.kind === 'Deployment',
);
assert(deployments.length === 12, 'expected twelve application deployments');
for (const deployment of deployments) {
  const spec = deployment.spec.template.spec;
  assert(
    spec.automountServiceAccountToken === false,
    `${deployment.metadata.name} mounts a service account token`,
  );
  assert(
    spec.securityContext?.runAsNonRoot === true,
    `${deployment.metadata.name} may run as root`,
  );
  for (const container of spec.containers) {
    assert(
      container.securityContext?.allowPrivilegeEscalation === false,
      `${deployment.metadata.name} allows privilege escalation`,
    );
    assert(
      container.securityContext?.readOnlyRootFilesystem === true,
      `${deployment.metadata.name} has a writable root filesystem`,
    );
    assert(
      container.securityContext?.capabilities?.drop?.includes('ALL'),
      `${deployment.metadata.name} retains Linux capabilities`,
    );
    assert(
      !container.image.endsWith(':latest'),
      `${deployment.metadata.name} uses a latest image tag`,
    );
    if (deploymentMode)
      validateDeploymentImage(container.image, deployment.metadata.name);
  }
}

for (const job of resources.filter(
  (resource) => resource.kind === 'CronJob' || resource.kind === 'Job',
)) {
  for (const container of job.spec.jobTemplate?.spec?.template?.spec
    ?.containers ??
    job.spec.template?.spec?.containers ??
    []) {
    if (deploymentMode)
      validateDeploymentImage(container.image, job.metadata.name);
  }
}

const services = resources.filter((resource) => resource.kind === 'Service');
assert(
  services.every((service) => service.spec.type === 'ClusterIP'),
  'all services must remain private ClusterIP services',
);

const ingresses = resources.filter((resource) => resource.kind === 'Ingress');
const allowedBackends = new Set([
  'excellore-shell',
  'excellore-profile-bff',
  'excellore-access-bff',
  'excellore-platform-bff',
  'excellore-notifications-api',
  'excellore-identity-proxy',
]);
const ingressBackends = ingresses.flatMap((ingress) =>
  ingress.spec.rules.flatMap((rule) =>
    rule.http.paths.map((path) => path.backend.service.name),
  ),
);
assert(ingressBackends.length === 6, 'expected six public ingress backends');
assert(
  ingressBackends.every((name) => allowedBackends.has(name)),
  'an unapproved API, worker, or Keycloak service is publicly routed',
);

const notificationIngress = ingresses.find(
  (ingress) => ingress.metadata.name === 'excellore-notifications-webhook',
);
const notificationPath =
  notificationIngress?.spec?.rules?.[0]?.http?.paths?.[0];
assert(
  notificationPath?.path === '/webhooks/v1/resend' &&
    notificationPath?.pathType === 'Exact' &&
    notificationPath?.backend?.service?.name === 'excellore-notifications-api',
  'Notifications ingress must expose only the exact Resend webhook path',
);
assert(
  notificationIngress?.metadata?.annotations?.[
    'nginx.ingress.kubernetes.io/enable-access-log'
  ] === 'false',
  'Notifications webhook ingress must suppress request metadata logs',
);

const identityIngress = ingresses.find(
  (ingress) => ingress.metadata.name === 'excellore-identity',
);
assert(
  identityIngress?.metadata.annotations?.[
    'nginx.ingress.kubernetes.io/enable-access-log'
  ] === 'false',
  'identity ingress must suppress action-link URLs',
);

const proxyConfig = resources.find(
  (resource) =>
    resource.kind === 'ConfigMap' &&
    resource.metadata.name === 'excellore-identity-proxy',
);
for (const blocked of ['/admin', '/realms/master', '/internal'])
  assert(
    proxyConfig?.data?.['nginx.conf']?.includes(blocked),
    `identity proxy must block ${blocked}`,
  );
assert(
  proxyConfig?.data?.['nginx.conf']?.includes('access_log off'),
  'identity proxy must not log action-link URLs',
);

const configMaps = resources.filter(
  (resource) => resource.kind === 'ConfigMap',
);
for (const config of configMaps) {
  for (const key of Object.keys(config.data ?? {}))
    assert(
      !/(?:secret|password|credential|api[_-]?key|connectionstrings?)/iu.test(
        key,
      ),
      `${config.metadata.name} contains a secret-shaped ConfigMap key`,
    );
}
const profileConfig = configMaps.find(
  (config) => config.metadata.name === 'excellore-profile-api',
);
const tokenEndpoint = new URL(
  profileConfig?.data?.Authz__TokenEndpoint ?? 'invalid:',
);
assert(
  tokenEndpoint.protocol === 'https:' &&
    tokenEndpoint.username === '' &&
    tokenEndpoint.password === '' &&
    tokenEndpoint.search === '' &&
    tokenEndpoint.hash === '' &&
    tokenEndpoint.pathname ===
      '/realms/excellore/protocol/openid-connect/token',
  'Profile Authz token endpoint must be the public credential-free realm endpoint',
);
const notificationsConfig = configMaps.find(
  (config) => config.metadata.name === 'excellore-notifications-worker',
);
const notificationsTokenEndpoint = new URL(
  notificationsConfig?.data?.Authz__TokenEndpoint ?? 'invalid:',
);
assert(
  notificationsTokenEndpoint.protocol === 'https:' &&
    notificationsTokenEndpoint.username === '' &&
    notificationsTokenEndpoint.password === '' &&
    notificationsTokenEndpoint.search === '' &&
    notificationsTokenEndpoint.hash === '' &&
    notificationsTokenEndpoint.pathname ===
      '/realms/excellore/protocol/openid-connect/token',
  'Notifications Authz token endpoint must be the public credential-free realm endpoint',
);

for (const [component, prefix] of [
  ['profile-api', 'Identity'],
  ['access-api', 'IdentityAdmin'],
  ['profile-worker', 'IdentityDirectory'],
  ['access-worker', 'IdentityDirectory'],
]) {
  const config = resources.find(
    (resource) =>
      resource.kind === 'ConfigMap' &&
      resource.metadata.name === `excellore-${component}`,
  );
  const authority = new URL(
    config?.data?.[`${prefix}__Authority`] ?? 'invalid:',
  );
  const allowedHost = config?.data?.[`${prefix}__PrivateHttpHost`];
  assert(
    authority.protocol === 'http:' &&
      authority.hostname === allowedHost &&
      allowedHost?.endsWith('.svc.cluster.local'),
    `${component} private identity authority must match its exact cluster-service host`,
  );
}

const policies = resources.filter(
  (resource) => resource.kind === 'NetworkPolicy',
);
const deny = policies.find(
  (policy) => policy.metadata.name === 'excellore-default-deny',
);
assert(
  deny && Object.keys(deny.spec.podSelector).length === 0,
  'a namespace-wide default-deny policy is required',
);
assert(
  deny.spec.policyTypes.includes('Ingress') &&
    deny.spec.policyTypes.includes('Egress'),
  'default deny must cover ingress and egress',
);

const externalSecrets = resources.filter(
  (resource) => resource.kind === 'ExternalSecret',
);
assert(
  externalSecrets.length === 12,
  'each secret-bearing workload needs its own ExternalSecret',
);
assert(
  new Set(externalSecrets.map((secret) => secret.spec.target.name)).size === 12,
  'workloads must not share runtime Secrets',
);

if (deploymentMode) {
  const rendered = JSON.stringify(resources);
  assert(
    !rendered.includes('.example.invalid'),
    'deployment manifests contain example.invalid placeholders',
  );
  assert(
    !rendered.includes('sha256:' + '0'.repeat(64)),
    'deployment manifests contain all-zero image digests',
  );
}

console.log(`Validated ${resources.length} production Kubernetes resources.`);

function walk(directory) {
  return readdirSync(directory).flatMap((entry) => {
    const path = resolve(directory, entry);
    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function validateDeploymentImage(image, workload) {
  assert(
    /@sha256:[0-9a-f]{64}$/u.test(image),
    `${workload} image is not pinned by sha256 digest`,
  );
}
