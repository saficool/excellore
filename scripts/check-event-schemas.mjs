import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const contractsDir = fileURLToPath(
  new URL('../shared/dotnet/Excellore.Contracts/', import.meta.url),
);
const schemasDir = join(contractsDir, 'Schemas');
const examplesDir = join(contractsDir, 'Examples');
const schemaFiles = readdirSync(schemasDir).filter((name) =>
  name.endsWith('.schema.json'),
);
assert.ok(schemaFiles.length > 0, 'No event schemas found');
let validatedExamples = 0;

const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);

for (const file of schemaFiles) {
  const eventType = file.slice(0, -'.schema.json'.length);
  const schema = JSON.parse(readFileSync(join(schemasDir, file), 'utf8'));
  const example = JSON.parse(
    readFileSync(join(examplesDir, `${eventType}.example.json`), 'utf8'),
  );

  assert.equal(schema.$schema, 'https://json-schema.org/draft/2020-12/schema');
  assert.equal(schema.$id, `urn:excellore:event:${eventType}`);
  assert.equal(schema.title, eventType);
  assert.equal(example.type, eventType);

  const validate = ajv.compile(schema);
  assert.ok(validate(example), `${eventType}: valid example was rejected`);
  validatedExamples += 1;

  const invalidExamples = [
    { ...example, id: 'not-a-uuid' },
    { ...example, type: `${eventType}.unknown` },
    { ...example, data: undefined },
  ];
  for (const required of schema.properties.data.required) {
    invalidExamples.push({
      ...example,
      data: { ...example.data, [required]: undefined },
    });
  }
  if (eventType.startsWith('identity.user.')) {
    invalidExamples.push(
      { ...example, tenantid: '22222222-2222-4222-8222-222222222222' },
      { ...example, data: { ...example.data, email: 'personal@example.test' } },
      { ...example, data: { ...example.data, givenName: 'Personal' } },
    );
  }
  if (
    [
      'tenant.created.v1',
      'tenant.updated.v1',
      'tenant.app.enabled.v1',
      'tenant.app.disabled.v1',
    ].includes(eventType)
  ) {
    invalidExamples.push(
      { ...example, data: { ...example.data, slug: 'identifying-slug' } },
      {
        ...example,
        data: { ...example.data, features: { freeForm: 'value' } },
      },
    );
  }
  if (eventType.startsWith('tenant.member.')) {
    invalidExamples.push(
      { ...example, data: { ...example.data, roles: ['custom role'] } },
      { ...example, data: { ...example.data, roleNames: ['custom role'] } },
    );
  }
  if (eventType.startsWith('access.role.')) {
    invalidExamples.push(
      { ...example, data: { ...example.data, name: 'custom value' } },
      {
        ...example,
        data: { ...example.data, permissions: ['app.resource.read'] },
      },
    );
  }
  if (eventType.startsWith('access.assignment.')) {
    invalidExamples.push(
      { ...example, data: { ...example.data, expiresAt: null } },
      { ...example, data: { ...example.data, expiresAt: '2026-12-01' } },
      { ...example, data: { ...example.data, name: 'personal value' } },
      { ...example, data: { ...example.data, description: 'personal value' } },
      { ...example, data: { ...example.data, email: 'personal@example.test' } },
      { ...example, data: { ...example.data, reason: 'personal value' } },
      { ...example, data: { ...example.data, roleName: 'personal value' } },
      {
        ...example,
        data: { ...example.data, permissions: ['app.resource.read'] },
      },
    );
  }
  if (eventType.startsWith('access.supportgrant.')) {
    invalidExamples.push(
      { ...example, data: { ...example.data, expiresAt: null } },
      { ...example, data: { ...example.data, expiresAt: '2026-01-01' } },
      { ...example, data: { ...example.data, description: 'personal value' } },
      { ...example, data: { ...example.data, staffName: 'personal value' } },
      { ...example, data: { ...example.data, email: 'personal@example.test' } },
      { ...example, data: { ...example.data, reason: 'personal value' } },
      {
        ...example,
        data: { ...example.data, permissions: ['app.resource.read'] },
      },
    );
  }
  if (eventType.startsWith('profile.') && !eventType.includes('.erasure.')) {
    invalidExamples.push(
      { ...example, tenantid: '22222222-2222-4222-8222-222222222222' },
      { ...example, data: { ...example.data, email: 'personal@example.test' } },
      { ...example, data: { ...example.data, displayName: 'personal value' } },
      {
        ...example,
        data: { ...example.data, downloadUrl: 'https://example.test/export' },
      },
      {
        ...example,
        data: { ...example.data, signedUrl: 'https://example.test/export' },
      },
    );
  }
  if (eventType.includes('export')) {
    invalidExamples.push(
      { ...example, data: { ...example.data, email: 'personal@example.test' } },
      {
        ...example,
        data: { ...example.data, objectKey: 'exports/private.json' },
      },
      { ...example, data: { ...example.data, token: 'secret' } },
    );
  }
  if (eventType.endsWith('.erasure.confirmed.v1')) {
    const userExample = JSON.parse(
      readFileSync(join(examplesDir, `${eventType}.user.example.json`), 'utf8'),
    );
    assert.equal(userExample.type, eventType);
    assert.ok(validate(userExample), `${eventType}: user example was rejected`);
    validatedExamples += 1;
    invalidExamples.push(
      { ...example, tenantid: undefined },
      {
        ...example,
        data: {
          ...example.data,
          userId: '55555555-5555-4555-8555-555555555555',
        },
      },
      { ...userExample, tenantid: '22222222-2222-4222-8222-222222222222' },
      {
        ...userExample,
        data: {
          ...userExample.data,
          tenantId: '22222222-2222-4222-8222-222222222222',
        },
      },
      { ...userExample, data: { ...userExample.data, userId: undefined } },
      { ...example, data: { ...example.data, service: 'other' } },
      { ...example, data: { ...example.data, scope: 'other' } },
      { ...example, data: { ...example.data, email: 'personal@example.test' } },
    );
  }
  for (const [index, invalid] of invalidExamples.entries()) {
    assert.equal(
      validate(invalid),
      false,
      `${eventType}: invalid event case ${index} was accepted`,
    );
  }

  assert.ok(
    validate({
      ...example,
      futureField: true,
      data: { ...example.data, futureField: true },
    }),
    `${eventType}: unknown fields were rejected`,
  );
}

console.log(
  `Validated ${schemaFiles.length} event schema(s) and ${validatedExamples} example(s).`,
);
