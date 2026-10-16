import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const [script, ...args] = process.argv.slice(2);
assert.ok(script, 'Usage: node scripts/with-local-ca.mjs <script> [...args]');
const root = fileURLToPath(new URL('../', import.meta.url));
const match = readFileSync(new URL('../.env', import.meta.url), 'utf8').match(
  /^LOCAL_CA_CERT=(.+)$/m,
);
const certificate = match?.[1].trim();
assert.ok(
  certificate && existsSync(certificate),
  'Run node scripts/setup-local-https.mjs first.',
);
const child = spawn(process.execPath, [script, ...args], {
  cwd: root,
  stdio: 'inherit',
  env: { ...process.env, NODE_EXTRA_CA_CERTS: certificate },
});
child.on('error', () => {
  process.exitCode = 1;
});
child.on('exit', (code) => {
  process.exitCode = code ?? 1;
});
