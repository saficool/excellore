import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const env = Object.fromEntries(
  (await readFile(new URL('../.env', import.meta.url), 'utf8'))
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => [
      line.slice(0, line.indexOf('=')),
      line.slice(line.indexOf('=') + 1),
    ]),
);
assert.ok(
  env.ACCESS_APP_PASSWORD && env.SVC_ACCESS_ADMIN_SECRET,
  'Local stack is not initialized',
);
assert.equal(
  new URL(env.ACCESS_ORIGIN).hostname.endsWith('.localhost'),
  true,
  'Local Access API requires a local app origin',
);
const child = spawn(
  'dotnet',
  [
    'run',
    '--no-build',
    '--no-launch-profile',
    '--project',
    'services/access/Excellore.Access.Api',
    '--urls',
    'http://127.0.0.1:5002',
  ],
  {
    cwd: root,
    stdio: 'inherit',
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      EXCELLORE_INSECURE_LOCAL_DEV: 'true',
      Auth__Authority: 'http://auth.excellore.localhost:8080/realms/excellore',
      ConnectionStrings__Access: `Host=127.0.0.1;Port=5434;Database=access;Username=access_app;Password=${env.ACCESS_APP_PASSWORD}`,
      SVC_ACCESS_ADMIN_SECRET: env.SVC_ACCESS_ADMIN_SECRET,
      AuditExportStore__Endpoint: 'http://127.0.0.1:8333',
      AuditExportStore__AccessKey: env.OBJECTSTORE_ACCESS_KEY,
      AuditExportStore__SecretKey: env.OBJECTSTORE_SECRET_KEY,
    },
  },
);
child.on('error', () => {
  process.stderr.write('Unable to start the local Access API.\n');
  process.exitCode = 1;
});
child.on('exit', (code) => {
  process.exitCode = code ?? 1;
});
