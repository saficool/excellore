import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const service = process.argv[2];
assert.ok(
  ['access', 'profile'].includes(service),
  'Usage: node scripts/run-worker-local.mjs access|profile',
);
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
const prefix = service.toUpperCase();
assert.ok(
  env[`${prefix}_PUBLISHER_PASSWORD`] && env[`${prefix}_WORKER_PASSWORD`],
  'Run scripts/dev-up.sh to create local worker credentials',
);
assert.ok(
  env.RABBITMQ_USER && env.RABBITMQ_PASSWORD,
  'Local RabbitMQ credentials are missing',
);
assert.ok(env[`WORKER_${prefix}_SECRET`], 'Local worker identity is missing');
const broker = new URL('amqp://127.0.0.1:5672/');
broker.username = env.RABBITMQ_USER;
broker.password = env.RABBITMQ_PASSWORD;
const port = service === 'access' ? 5434 : 5433;
const connection = (role, password) =>
  `Host=127.0.0.1;Port=${port};Database=${service};Username=${role};Password=${password}`;
const workerEnv = {
  ...process.env,
  DOTNET_ENVIRONMENT: 'Development',
  EXCELLORE_INSECURE_LOCAL_DEV: 'true',
  Auth__Authority: `${env.KEYCLOAK_ORIGIN}/realms/excellore`,
  IdentityDirectory__ClientSecret: env[`WORKER_${prefix}_SECRET`],
  Messaging__RabbitMqUri: broker.toString(),
  [`ConnectionStrings__${service === 'access' ? 'Access' : 'Profile'}Publisher`]:
    connection(`${service}_publisher`, env[`${prefix}_PUBLISHER_PASSWORD`]),
  [`ConnectionStrings__${service === 'access' ? 'Access' : 'Profile'}Worker`]:
    connection(`${service}_worker`, env[`${prefix}_WORKER_PASSWORD`]),
  ExportStore__Endpoint: 'http://127.0.0.1:8333',
  ExportStore__AccessKey: env.OBJECTSTORE_ACCESS_KEY,
  ExportStore__SecretKey: env.OBJECTSTORE_SECRET_KEY,
};
assert.match(env[`${prefix}_WORKER_ACTOR_ID`] ?? '', /^[0-9a-f-]{36}$/i);
workerEnv.Worker__ActorId = env[`${prefix}_WORKER_ACTOR_ID`];
if (service === 'access') {
  workerEnv.AuditExportStore__Endpoint = 'http://127.0.0.1:8333';
  workerEnv.AuditExportStore__AccessKey = env.OBJECTSTORE_ACCESS_KEY;
  workerEnv.AuditExportStore__SecretKey = env.OBJECTSTORE_SECRET_KEY;
}
const child = spawn(
  'dotnet',
  [
    'run',
    '--no-build',
    '--no-launch-profile',
    '--project',
    `services/${service}/Excellore.${service === 'access' ? 'Access' : 'Profile'}.Worker`,
  ],
  { cwd: root, stdio: 'inherit', env: workerEnv },
);
child.on('error', () => {
  process.stderr.write(`Unable to start the local ${service} worker.\n`);
  process.exitCode = 1;
});
child.on('exit', (code) => {
  process.exitCode = code ?? 1;
});
