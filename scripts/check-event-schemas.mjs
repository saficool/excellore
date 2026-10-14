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
  for (const invalid of invalidExamples) {
    assert.equal(
      validate(invalid),
      false,
      `${eventType}: invalid event was accepted`,
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

console.log(`Validated ${schemaFiles.length} event schema(s) and examples.`);
