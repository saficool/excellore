import assert from 'node:assert/strict';

const requiredSmtpKeys = [
  'EMAIL_SMTP_HOST',
  'EMAIL_SMTP_PORT',
  'KEYCLOAK_EMAIL_FROM_ADDRESS',
  'KEYCLOAK_EMAIL_SMTP_USERNAME',
  'KEYCLOAK_EMAIL_SMTP_PASSWORD',
];

export function applyLocalKeycloakEmailConfig(realm, env) {
  const transport = env.KEYCLOAK_EMAIL_TRANSPORT || 'mailpit';
  assert.ok(
    transport === 'mailpit' || transport === 'smtp',
    'KEYCLOAK_EMAIL_TRANSPORT must be mailpit or smtp.',
  );
  if (transport === 'mailpit') return realm;

  for (const key of requiredSmtpKeys)
    assert.ok(
      env[key] && env[key] !== '__GENERATE__',
      `Missing local email setting ${key} in .env.`,
    );

  const port = Number(env.EMAIL_SMTP_PORT);
  assert.ok(
    /^[a-z0-9.-]+$/iu.test(env.EMAIL_SMTP_HOST) &&
      env.EMAIL_SMTP_HOST.length <= 253,
    'Local email SMTP host is invalid.',
  );
  assert.ok(
    Number.isInteger(port) && port >= 1 && port <= 65535,
    'Local email SMTP port is invalid.',
  );
  assert.ok(
    /^[^\s@]+@[^\s@]+\.[^\s@]+$/u.test(env.KEYCLOAK_EMAIL_FROM_ADDRESS),
    'Local Keycloak sender address is invalid.',
  );

  realm.smtpServer = {
    host: env.EMAIL_SMTP_HOST,
    port: String(port),
    from: env.KEYCLOAK_EMAIL_FROM_ADDRESS,
    fromDisplayName: 'Excellore Accounts',
    auth: 'true',
    user: env.KEYCLOAK_EMAIL_SMTP_USERNAME,
    password: env.KEYCLOAK_EMAIL_SMTP_PASSWORD,
    ssl: 'false',
    starttls: 'true',
  };
  return realm;
}
