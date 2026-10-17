import assert from 'node:assert/strict';

export function localApplicationEmailEnvironment(env) {
  const transport = env.APPLICATION_EMAIL_TRANSPORT || 'mailpit';
  assert.ok(
    transport === 'mailpit' || transport === 'smtp',
    'APPLICATION_EMAIL_TRANSPORT must be mailpit or smtp.',
  );
  if (transport === 'mailpit') return {};

  for (const key of [
    'EMAIL_SMTP_HOST',
    'EMAIL_SMTP_PORT',
    'APPLICATION_EMAIL_FROM_ADDRESS',
    'KEYCLOAK_EMAIL_SMTP_USERNAME',
    'KEYCLOAK_EMAIL_SMTP_PASSWORD',
  ])
    assert.ok(
      env[key] && env[key] !== '__GENERATE__',
      `Missing local email setting ${key} in .env.`,
    );
  const port = Number(env.EMAIL_SMTP_PORT);
  assert.ok(
    Number.isInteger(port) && port >= 1 && port <= 65535,
    'Local email SMTP port is invalid.',
  );
  assert.ok(
    /^[^\s@]+@[^\s@]+\.[^\s@]+$/u.test(env.APPLICATION_EMAIL_FROM_ADDRESS),
    'Local application sender address is invalid.',
  );

  return {
    Email__SmtpHost: env.EMAIL_SMTP_HOST,
    Email__SmtpPort: String(port),
    Email__SmtpSecurity: 'StartTls',
    Email__SmtpUsername: env.KEYCLOAK_EMAIL_SMTP_USERNAME,
    Email__SmtpPassword: env.KEYCLOAK_EMAIL_SMTP_PASSWORD,
    Email__FromAddress: env.APPLICATION_EMAIL_FROM_ADDRESS,
    Email__FromName: 'Excellore',
  };
}
