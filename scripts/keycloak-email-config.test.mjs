import assert from 'node:assert/strict';
import test from 'node:test';
import { applyLocalKeycloakEmailConfig } from './keycloak-email-config.mjs';

const mailpitRealm = () => ({
  smtpServer: {
    host: 'mailpit',
    port: '1025',
    auth: 'false',
    ssl: 'false',
    starttls: 'false',
  },
});

test('mailpit remains the safe local default', () => {
  const realm = mailpitRealm();
  applyLocalKeycloakEmailConfig(realm, {});
  assert.equal(realm.smtpServer.host, 'mailpit');
});

test('authenticated SMTP replaces Mailpit without exposing configuration', () => {
  const realm = mailpitRealm();
  applyLocalKeycloakEmailConfig(realm, {
    KEYCLOAK_EMAIL_TRANSPORT: 'smtp',
    EMAIL_SMTP_HOST: 'smtp.example.test',
    EMAIL_SMTP_PORT: '587',
    KEYCLOAK_EMAIL_FROM_ADDRESS: 'accounts@example.test',
    KEYCLOAK_EMAIL_SMTP_USERNAME: 'smtp-user',
    KEYCLOAK_EMAIL_SMTP_PASSWORD: 'smtp-secret',
  });
  assert.deepEqual(realm.smtpServer, {
    host: 'smtp.example.test',
    port: '587',
    from: 'accounts@example.test',
    fromDisplayName: 'Excellore Accounts',
    auth: 'true',
    user: 'smtp-user',
    password: 'smtp-secret',
    ssl: 'false',
    starttls: 'true',
  });
});

test('authenticated SMTP fails closed when a required value is absent', () => {
  assert.throws(
    () =>
      applyLocalKeycloakEmailConfig(mailpitRealm(), {
        KEYCLOAK_EMAIL_TRANSPORT: 'smtp',
      }),
    /Missing local email setting/u,
  );
});
