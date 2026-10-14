import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readFile, rm } from 'node:fs/promises';
import { resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';

const run = promisify(execFile);
const root = resolve(import.meta.dirname, '../../..');
const app = `zz-template-${randomBytes(4).toString('hex')}`;
const target = resolve(root, 'apps', app);

test('generator renders a runnable app with exact client and callback configuration', async () => {
  assert.equal(target, resolve(root, 'apps', app));
  try {
    await run(
      process.execPath,
      [
        resolve(
          root,
          'packages/create-excellore-app/bin/create-excellore-app.mjs',
        ),
        app,
        '3999',
      ],
      { cwd: root },
    );
    const packageJson = JSON.parse(
      await readFile(resolve(target, 'package.json'), 'utf8'),
    );
    const env = await readFile(resolve(target, '.env.example'), 'utf8');
    assert.equal(packageJson.name, `@excellore/${app}`);
    assert.equal(packageJson.dependencies.next, '16.4.0');
    assert.match(env, new RegExp(`OIDC_CLIENT_ID=excellore-${app}`));
    assert.match(
      env,
      new RegExp(`APP_BASE_URL=http://${app}\\.excellore\\.localhost:3999`),
    );
    assert.ok(
      (
        await readFile(resolve(target, 'app/auth/[action]/route.ts'), 'utf8')
      ).includes('handlers.callback(canonical)'),
    );
  } finally {
    await rm(target, { recursive: true, force: true });
  }
});
