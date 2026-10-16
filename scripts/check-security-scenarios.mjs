import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const scenarios = JSON.parse(
  await readFile('infra/security/wp13-scenarios.json', 'utf8'),
);
assert.equal(
  scenarios.length,
  15,
  'WP-13 requires exactly 15 security scenarios',
);
assert.deepEqual(
  scenarios.map(({ id }) => id),
  Array.from({ length: 15 }, (_, index) => index + 1),
  'WP-13 scenario IDs must be complete and ordered',
);

for (const scenario of scenarios) {
  assert.match(scenario.name, /\S/, `Scenario ${scenario.id} needs a name`);
  assert.ok(
    scenario.evidence.length > 0,
    `Scenario ${scenario.id} needs evidence`,
  );
  for (const evidence of scenario.evidence) {
    const source = await readFile(evidence.path, 'utf8');
    assert.ok(
      source.includes(evidence.contains),
      `Scenario ${scenario.id} evidence is stale: ${evidence.path}`,
    );
  }
}

const faults = JSON.parse(
  await readFile('infra/security/fault-injection.json', 'utf8'),
);
assert.deepEqual(
  faults.map(({ dependency }) => dependency).sort(),
  ['Access authority', 'Keycloak', 'RabbitMQ', 'Valkey'],
  'Fault injection must cover every handbook dependency',
);
for (const fault of faults) {
  const source = await readFile(fault.path, 'utf8');
  assert.ok(
    source.includes(fault.contains),
    `Fault-injection evidence is stale: ${fault.dependency}`,
  );
}

for (const path of [
  'apps/shell/proxy.ts',
  'apps/profile/proxy.ts',
  'apps/access/proxy.ts',
  'packages/create-excellore-app/template/proxy.ts',
]) {
  const source = await readFile(path, 'utf8');
  assert.ok(
    source.includes('createBffSecurityHeaders'),
    `${path} must use shared BFF headers`,
  );
}

const load = await readFile('tests/load/wp13.js', 'utf8');
for (const scenario of [
  'login_burst',
  'authz_resolve',
  'refresh_storm',
  'bulk_assignment',
])
  assert.ok(load.includes(`${scenario}:`), `Missing k6 scenario: ${scenario}`);

process.stdout.write(
  'All 15 WP-13 scenarios and four fault boundaries have executable CI evidence.\n',
);
