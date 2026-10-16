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
for (const key of [
  'PROFILE_APP_PASSWORD',
  'SVC_PROFILE_AUTHZ_SECRET',
  'SVC_PROFILE_IDENTITY_SECRET',
  'RABBITMQ_USER',
  'RABBITMQ_PASSWORD',
  'OBJECTSTORE_ACCESS_KEY',
  'OBJECTSTORE_SECRET_KEY',
])
  assert.ok(env[key] && env[key] !== '__GENERATE__', `Missing ${key}`);
const child = spawn(
  'dotnet',
  [
    'run',
    '--no-build',
    '--no-launch-profile',
    '--project',
    'services/profile/Excellore.Profile.Api',
    '--urls',
    'http://127.0.0.1:5001',
  ],
  {
    cwd: root,
    stdio: 'inherit',
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      EXCELLORE_INSECURE_LOCAL_DEV: 'true',
      Auth__Authority: 'http://auth.excellore.localhost:8080/realms/excellore',
      ConnectionStrings__Profile: `Host=127.0.0.1;Port=5433;Database=profile;Username=profile_app;Password=${env.PROFILE_APP_PASSWORD}`,
      Authz__Valkey: '127.0.0.1:6379',
      Authz__AccessInternalUrl: 'http://127.0.0.1:5002',
      Authz__TokenEndpoint:
        'http://auth.excellore.localhost:8080/realms/excellore/protocol/openid-connect/token',
      Authz__ClientId: 'svc-profile-authz',
      Authz__ClientSecret: env.SVC_PROFILE_AUTHZ_SECRET,
      Authz__RabbitMq: `amqp://${encodeURIComponent(env.RABBITMQ_USER)}:${encodeURIComponent(env.RABBITMQ_PASSWORD)}@127.0.0.1:5672`,
      Identity__ClientSecret: env.SVC_PROFILE_IDENTITY_SECRET,
      AvatarStore__Endpoint: 'http://127.0.0.1:8333',
      AvatarStore__AccessKey: env.OBJECTSTORE_ACCESS_KEY,
      AvatarStore__SecretKey: env.OBJECTSTORE_SECRET_KEY,
      AvatarStore__Bucket: 'excellore-avatars',
      ExportStore__Endpoint: 'http://127.0.0.1:8333',
      ExportStore__AccessKey: env.OBJECTSTORE_ACCESS_KEY,
      ExportStore__SecretKey: env.OBJECTSTORE_SECRET_KEY,
    },
  },
);
child.on('error', () => {
  process.stderr.write('Unable to start the local Profile API.\n');
  process.exitCode = 1;
});
child.on('exit', (code) => {
  process.exitCode = code ?? 1;
});
