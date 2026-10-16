import { chmodSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';

const source = new URL('../infra/keycloak/realm-dev.json', import.meta.url);
const destination = new URL(
  '../.local/keycloak/realm-dev.json',
  import.meta.url,
);
const env = Object.fromEntries(
  readFileSync(new URL('../.env', import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => /^[A-Z][A-Z0-9_]*=/.test(line))
    .map((line) => {
      const separator = line.indexOf('=');
      return [line.slice(0, separator), line.slice(separator + 1)];
    }),
);

const rendered = readFileSync(source, 'utf8').replace(
  /\$\{([A-Z][A-Z0-9_]*)\}/g,
  (_, key) => {
    if (!env[key] || env[key] === '__GENERATE__') {
      throw new Error(`Missing local realm setting ${key} in .env.`);
    }
    return env[key];
  },
);
if (rendered.includes('${')) {
  throw new Error('Unresolved local realm placeholder.');
}
JSON.parse(rendered);
mkdirSync(new URL('../.local/keycloak/', import.meta.url), { recursive: true });
writeFileSync(destination, rendered, { mode: 0o640 });
chmodSync(destination, 0o640);
console.log('Rendered ignored local Keycloak realm.');
