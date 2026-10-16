import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { delimiter, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const apps = [
  ['shell', 3000],
  ['profile', 3001],
  ['access', 3002],
];
const env = Object.fromEntries(
  readFileSync(new URL('../.env', import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => {
      const separator = line.indexOf('=');
      return [line.slice(0, separator), line.slice(separator + 1)];
    }),
);
assert.ok(
  env.LOCAL_CA_CERT && existsSync(env.LOCAL_CA_CERT),
  'Run node scripts/setup-local-https.mjs first.',
);

for (const [app] of apps) {
  const configured = spawnSync(
    process.execPath,
    ['scripts/setup-next-local.mjs', app],
    { cwd: root, stdio: 'inherit' },
  );
  assert.equal(configured.status, 0, `Unable to configure ${app}`);
}

const childEnv = {
  ...process.env,
  NODE_EXTRA_CA_CERTS: env.LOCAL_CA_CERT,
  PATH: `${dirname(process.execPath)}${delimiter}${process.env.PATH ?? ''}`,
};
const children = [
  spawn(process.execPath, ['scripts/local-https-proxy.mjs'], {
    cwd: root,
    env: childEnv,
    stdio: 'inherit',
  }),
  ...apps.map(([app, port]) =>
    spawn(
      process.execPath,
      [
        join(root, 'apps', app, 'node_modules', 'next', 'dist', 'bin', 'next'),
        'dev',
        '--port',
        String(port),
      ],
      {
        cwd: join(root, 'apps', app),
        env: childEnv,
        stdio: 'inherit',
      },
    ),
  ),
];

let stopping = false;
function stop(code = 0) {
  if (stopping) return;
  stopping = true;
  for (const child of children) if (!child.killed) child.kill();
  process.exitCode = code;
}
for (const child of children) {
  child.on('error', () => stop(1));
  child.on('exit', (code) => stop(code ?? 1));
}
process.on('SIGINT', () => stop(0));
process.on('SIGTERM', () => stop(0));
