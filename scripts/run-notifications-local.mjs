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
  env.NOTIFICATIONS_WORKER_PASSWORD && env.SVC_NOTIFICATIONS_AUTHZ_SECRET,
  'Run scripts/dev-up.sh to create Notifications credentials',
);
const broker = new URL('amqp://127.0.0.1:5672/');
broker.username = env.RABBITMQ_USER;
broker.password = env.RABBITMQ_PASSWORD;
const child = spawn(
  'dotnet',
  [
    'run',
    '--no-build',
    '--project',
    'services/notifications/Excellore.Notifications.Worker',
  ],
  {
    cwd: root,
    stdio: 'inherit',
    env: {
      ...process.env,
      DOTNET_ENVIRONMENT: 'Development',
      EXCELLORE_INSECURE_LOCAL_DEV: 'true',
      Messaging__RabbitMqUri: broker.toString(),
      ConnectionStrings__NotificationsWorker: `Host=127.0.0.1;Port=5435;Database=notifications;Username=notifications_worker;Password=${env.NOTIFICATIONS_WORKER_PASSWORD}`,
      Authz__ClientSecret: env.SVC_NOTIFICATIONS_AUTHZ_SECRET,
    },
  },
);
child.on('error', () => {
  process.stderr.write('Unable to start the local Notifications worker.\n');
  process.exitCode = 1;
});
child.on('exit', (code) => {
  process.exitCode = code ?? 1;
});
