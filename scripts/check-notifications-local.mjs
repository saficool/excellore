import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { promisify } from 'node:util';

const run = promisify(execFile);
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
  env.RABBITMQ_USER && env.RABBITMQ_PASSWORD,
  'Run scripts/dev-up.sh first',
);

const compose = [
  'compose',
  '--env-file',
  '.env',
  '-f',
  'infra/compose/docker-compose.yml',
  'exec',
  '-T',
  'postgres-access',
  'psql',
  '-X',
  '-A',
  '-t',
  '-U',
  'postgres',
  '-d',
  'access',
];
const membership = await sql(
  compose,
  `SELECT m.tenant_id::text || '|' || m.user_id::text
     FROM tenant_members m
     JOIN tenants t ON t.id=m.tenant_id
     JOIN identities i ON i.user_id=m.user_id
    WHERE m.status='active' AND t.status='active'
      AND i.status='active' AND i.email_verified
    ORDER BY m.created_at LIMIT 1`,
);
const [tenantId, userId] = membership.split('|');
assert.match(
  tenantId ?? '',
  /^[0-9a-f-]{36}$/iu,
  'No active local tenant member',
);
assert.match(userId ?? '', /^[0-9a-f-]{36}$/iu, 'No verified local contact');

const eventId = randomUUID();
const payload = JSON.stringify({
  specversion: '1.0',
  id: eventId,
  type: 'access.assignment.granted.v1',
  source: 'excellore/access',
  time: new Date().toISOString(),
  tenantid: tenantId,
  correlationid: randomUUID(),
  actor: { sub: randomUUID(), type: 'machine' },
  data: { tenantId, roleId: randomUUID(), userId },
});
const before = await mailCount();
await publish(payload, eventId);

let delivered = false;
for (let attempt = 0; attempt < 30; attempt += 1) {
  const status = await notificationSql(
    `SELECT status FROM notification_deliveries WHERE event_id='${eventId}'::uuid`,
  );
  if (status === 'sent') {
    delivered = true;
    break;
  }
  await new Promise((resolve) => setTimeout(resolve, 250));
}
assert.ok(delivered, 'Notification was not delivered');

await publish(payload, eventId);
await new Promise((resolve) => setTimeout(resolve, 750));
assert.equal(
  await mailCount(),
  before + 1,
  'Duplicate event produced another email',
);
assert.equal(
  await notificationSql(
    `SELECT count(*) FROM notification_deliveries WHERE event_id='${eventId}'::uuid`,
  ),
  '1',
  'Duplicate event produced another delivery row',
);
console.log('Notifications local flow delivered one idempotent Mailpit email.');

async function publish(eventPayload, messageId) {
  const response = await fetch(
    'http://127.0.0.1:15672/api/exchanges/%2F/excellore.events/publish',
    {
      method: 'POST',
      headers: {
        authorization: `Basic ${Buffer.from(
          `${env.RABBITMQ_USER}:${env.RABBITMQ_PASSWORD}`,
        ).toString('base64')}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify({
        properties: {
          content_type: 'application/cloudevents+json',
          delivery_mode: 2,
          message_id: messageId,
          type: 'access.assignment.granted.v1',
        },
        routing_key: 'access.assignment.granted.v1',
        payload: eventPayload,
        payload_encoding: 'string',
      }),
    },
  );
  assert.equal(response.status, 200, 'RabbitMQ publish request');
  assert.equal(
    (await response.json()).routed,
    true,
    'Notification event was not routed',
  );
}

async function mailCount() {
  const response = await fetch('http://127.0.0.1:8025/api/v1/messages?limit=1');
  assert.equal(response.status, 200, 'Mailpit message list');
  const body = await response.json();
  assert.equal(typeof body.total, 'number', 'Mailpit total');
  return body.total;
}

async function notificationSql(query) {
  const args = [...compose];
  args[args.indexOf('postgres-access')] = 'postgres-notifications';
  args[args.indexOf('access')] = 'notifications';
  return sql(args, query);
}

async function sql(args, query) {
  const result = await run('docker', [...args, '-c', query], {
    cwd: new URL('../', import.meta.url),
    windowsHide: true,
    maxBuffer: 64 * 1024,
  });
  return result.stdout.trim();
}
