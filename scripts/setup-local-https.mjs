import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { X509Certificate } from 'node:crypto';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const hostnames = [
  'excellore.localhost',
  'profile.excellore.localhost',
  'access.excellore.localhost',
  'auth.excellore.localhost',
];
const certificate = join(root, '.local', 'certs', 'excellore.pem');
const privateKey = join(root, '.local', 'certs', 'excellore-key.pem');

function run(executable, args, options = {}) {
  return spawnSync(executable, args, {
    cwd: root,
    encoding: 'utf8',
    ...options,
  });
}

function findMkcert() {
  const override = process.env.MKCERT_PATH;
  if (override && existsSync(override)) return override;

  const direct = run('mkcert', ['-CAROOT']);
  if (direct.status === 0) return 'mkcert';

  if (process.platform === 'win32') {
    const candidates = [
      join(
        process.env.LOCALAPPDATA ?? '',
        'Microsoft',
        'WinGet',
        'Links',
        'mkcert.exe',
      ),
      join(process.env.USERPROFILE ?? '', 'scoop', 'shims', 'mkcert.exe'),
      'C:\\ProgramData\\chocolatey\\bin\\mkcert.exe',
    ];
    const packages = join(
      process.env.LOCALAPPDATA ?? '',
      'Microsoft',
      'WinGet',
      'Packages',
    );
    if (existsSync(packages)) {
      for (const entry of readdirSync(packages, { withFileTypes: true })) {
        if (entry.isDirectory() && entry.name.startsWith('FiloSottile.mkcert_'))
          candidates.push(join(packages, entry.name, 'mkcert.exe'));
      }
    }
    const found = candidates.find((candidate) => existsSync(candidate));
    if (found) return found;
  }

  throw new Error('mkcert is not installed or MKCERT_PATH is invalid.');
}

function certificateIsCurrent() {
  if (!existsSync(certificate) || !existsSync(privateKey)) return false;
  try {
    const parsed = new X509Certificate(readFileSync(certificate));
    const validForAnotherMonth =
      Date.parse(parsed.validTo) > Date.now() + 30 * 24 * 60 * 60 * 1000;
    return (
      validForAnotherMonth &&
      hostnames.every((hostname) => parsed.checkHost(hostname) === hostname)
    );
  } catch {
    return false;
  }
}

function setEnvValues(path, values) {
  let lines = readFileSync(path, 'utf8').split(/\r?\n/);
  for (const [key, value] of Object.entries(values)) {
    const index = lines.findIndex((line) => line.startsWith(`${key}=`));
    if (index >= 0) lines[index] = `${key}=${value}`;
    else lines.push(`${key}=${value}`);
  }
  while (lines.at(-1) === '') lines.pop();
  writeFileSync(path, `${lines.join('\n')}\n`, { mode: 0o600 });
}

const mkcert = findMkcert();
const caResult = run(mkcert, ['-CAROOT']);
assert.equal(caResult.status, 0, 'Unable to locate the mkcert local CA');
const caCertificate = resolve(caResult.stdout.trim(), 'rootCA.pem');
assert.ok(
  existsSync(caCertificate),
  'The mkcert CA is not installed. Run mkcert -install first.',
);

mkdirSync(dirname(certificate), { recursive: true });
if (!certificateIsCurrent()) {
  const result = run(
    mkcert,
    ['-cert-file', certificate, '-key-file', privateKey, ...hostnames],
    { stdio: 'inherit', encoding: undefined },
  );
  assert.equal(result.status, 0, 'Unable to generate the local certificate');
}

setEnvValues(join(root, '.env'), {
  KEYCLOAK_ORIGIN: 'https://auth.excellore.localhost',
  KEYCLOAK_ADMIN_ORIGIN: 'http://auth.excellore.localhost:8080',
  SHELL_ORIGIN: 'https://excellore.localhost',
  PROFILE_ORIGIN: 'https://profile.excellore.localhost',
  ACCESS_ORIGIN: 'https://access.excellore.localhost',
  LOCAL_CA_CERT: caCertificate,
});

for (const args of [
  ['scripts/render-dev-realm.mjs'],
  ['scripts/setup-next-local.mjs', 'shell'],
  ['scripts/setup-next-local.mjs', 'profile'],
  ['scripts/setup-next-local.mjs', 'access'],
]) {
  const result = run(process.execPath, args, {
    stdio: 'inherit',
    encoding: undefined,
    env: { ...process.env, NODE_EXTRA_CA_CERTS: caCertificate },
  });
  assert.equal(result.status, 0, `Local setup failed: ${args.join(' ')}`);
}

const docker = run('docker', ['info', '--format', '{{.ServerVersion}}']);
if (docker.status === 0) {
  const restarted = run(
    'docker',
    [
      'compose',
      '--env-file',
      '.env',
      '-f',
      'infra/compose/docker-compose.yml',
      'up',
      '-d',
      '--wait',
      '--wait-timeout',
      '300',
      'keycloak',
    ],
    { stdio: 'inherit', encoding: undefined },
  );
  assert.equal(restarted.status, 0, 'Unable to restart local Keycloak');
  const reconciled = run(
    process.execPath,
    ['scripts/sync-dev-access-redirect.mjs'],
    { stdio: 'inherit', encoding: undefined },
  );
  assert.equal(reconciled.status, 0, 'Unable to reconcile local BFF redirects');
}

console.log('Local HTTPS configuration is ready.');
