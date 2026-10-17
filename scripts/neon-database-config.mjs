import assert from 'node:assert/strict';

const services = new Set(['access', 'profile', 'notifications']);
const roles = new Set(['app', 'publisher', 'worker']);

export function neonConnectionString(
  env,
  service,
  role,
  { applicationName = `excellore-${service}-${role}` } = {},
) {
  assert.ok(services.has(service), `Unsupported Neon service: ${service}`);
  assert.ok(roles.has(role), `Unsupported Neon role: ${role}`);

  const prefix = service.toUpperCase();
  const endpoint =
    role === 'worker' && service === 'access' ? 'DIRECT' : 'POOLED';
  const host = required(env, `${prefix}_NEON_${endpoint}_HOST`);
  const database = required(env, `${prefix}_NEON_DATABASE`);
  const password = required(env, `${prefix}_${role.toUpperCase()}_PASSWORD`);

  validateHost(host, `${prefix}_NEON_${endpoint}_HOST`);
  validateSimple(database, `${prefix}_NEON_DATABASE`);
  validateSimple(applicationName, 'applicationName');

  return [
    ['Host', host],
    ['Port', '5432'],
    ['Database', database],
    ['Username', `${service}_${role}`],
    ['Password', password],
    ['SSL Mode', 'VerifyFull'],
    ['Channel Binding', 'Require'],
    ['Application Name', applicationName],
    ['Timeout', '15'],
    ['Command Timeout', '30'],
    ['Maximum Pool Size', endpoint === 'DIRECT' ? '10' : '20'],
  ]
    .map(([key, value]) => `${key}=${quote(value)}`)
    .join(';');
}

function required(env, key) {
  const value = env[key]?.trim();
  assert.ok(value && value !== '__GENERATE__', `Missing ${key}`);
  return value;
}

function validateHost(value, key) {
  assert.match(value, /^[a-z0-9.-]+$/iu, `${key} must contain a hostname only`);
  assert.ok(!value.includes('://'), `${key} must not be a connection URL`);
}

function validateSimple(value, key) {
  assert.match(
    value,
    /^[a-z0-9._-]+$/iu,
    `${key} contains unsupported characters`,
  );
}

function quote(value) {
  return `'${String(value).replaceAll("'", "''")}'`;
}
