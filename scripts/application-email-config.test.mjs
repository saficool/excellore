import assert from 'node:assert/strict';
import test from 'node:test';
import { localApplicationEmailEnvironment } from './application-email-config.mjs';

test('Mailpit leaves development appsettings in control', () => {
  assert.deepEqual(localApplicationEmailEnvironment({}), {});
});

test('authenticated application SMTP becomes process-only configuration', () => {
  assert.deepEqual(
    localApplicationEmailEnvironment({
      APPLICATION_EMAIL_TRANSPORT: 'smtp',
      EMAIL_SMTP_HOST: 'smtp.example.test',
      EMAIL_SMTP_PORT: '587',
      APPLICATION_EMAIL_FROM_ADDRESS: 'notifications@example.test',
      KEYCLOAK_EMAIL_SMTP_USERNAME: 'smtp-user',
      KEYCLOAK_EMAIL_SMTP_PASSWORD: 'smtp-secret',
    }),
    {
      Email__SmtpHost: 'smtp.example.test',
      Email__SmtpPort: '587',
      Email__SmtpSecurity: 'StartTls',
      Email__SmtpUsername: 'smtp-user',
      Email__SmtpPassword: 'smtp-secret',
      Email__FromAddress: 'notifications@example.test',
      Email__FromName: 'Excellore',
    },
  );
});

test('authenticated application SMTP fails closed when incomplete', () => {
  assert.throws(
    () =>
      localApplicationEmailEnvironment({
        APPLICATION_EMAIL_TRANSPORT: 'smtp',
      }),
    /Missing local email setting/u,
  );
});
