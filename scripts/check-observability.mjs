import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import YAML from 'yaml';

const requiredAlerts = [
  'TenantProbingDetected',
  'ElevatedUnauthorizedRate',
  'ElevatedForbiddenRate',
  'ElevatedNotFoundRate',
  'PlatformAdminUsed',
  'SupportGrantCreated',
  'UnexpectedServiceTokenHost',
  'AuditHashChainFailure',
  'ForcedRlsCheckFailed',
  'FailedLoginSpike',
  'RefreshFailures',
  'AccessResolveErrors',
  'AccessResolveLatency',
  'AuthorizationInvalidationLag',
  'KeycloakLatency',
  'KeycloakNodeLoss',
  'RabbitQueueBacklog',
  'RabbitDeadLetterGrowth',
  'CertificateExpiring',
  'SecretRotationOverdue',
];
const rules = YAML.parse(
  await readFile('infra/observability/prometheus-alerts.yml', 'utf8'),
).groups.flatMap((group) => group.rules);
assert.deepEqual(
  rules.map((rule) => rule.alert).sort(),
  [...requiredAlerts].sort(),
  'Prometheus rules must cover the handbook minimum alerts',
);
const probing = rules.find((rule) => rule.alert === 'TenantProbingDetected');
assert.match(probing.expr, /excellore_security_probing_alerts_total/);
assert.equal(probing.labels.severity, 'critical');
const tenantGateTests = await readFile(
  'shared/dotnet/Excellore.Authz.Tests/TenantGateTests.cs',
  'utf8',
);
assert.match(
  tenantGateTests,
  /Repeated_cross_tenant_not_found_responses_fire_anonymous_probing_alert/,
  'The probing alert must be exercised through repeated tenant-gate 404 responses',
);

for (const path of [
  'infra/observability/grafana/app-overview.json',
  'infra/observability/grafana/identity.json',
  'infra/observability/grafana/authorization.json',
]) {
  const dashboard = JSON.parse(await readFile(path, 'utf8'));
  assert.match(dashboard.title, /^Excellore /);
  assert.ok(dashboard.panels.length >= 4, `${path} needs at least four panels`);
  assert.equal(
    new Set(dashboard.panels.map(({ id }) => id)).size,
    dashboard.panels.length,
  );
  for (const panel of dashboard.panels)
    assert.ok(
      panel.targets.every(
        ({ expr }) => typeof expr === 'string' && expr.length > 10,
      ),
    );
}

const workflow = YAML.parse(await readFile('.github/workflows/ci.yml', 'utf8'));
for (const job of ['security', 'load-contract', 'security-e2e'])
  assert.ok(workflow.jobs[job], `CI security job is missing: ${job}`);
const securitySteps = workflow.jobs.security.steps
  .map((step) => [step.name, step.uses, step.run].filter(Boolean).join(' '))
  .join('\n');
assert.match(securitySteps, /zaproxy\/action-baseline@v0\.15\.0/);
assert.match(securitySteps, /aquasecurity\/trivy-action@v0\.36\.0/);
assert.match(securitySteps, /check-zap-report\.mjs/);

process.stdout.write(
  'Observability dashboards and minimum alert rules are complete.\n',
);
