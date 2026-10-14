#!/usr/bin/env node
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { resolve, join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const [app, portText, option] = process.argv.slice(2);
if (!app || !/^[a-z][a-z0-9-]*$/.test(app) || !portText || !/^\d{4,5}$/.test(portText)) {
  throw new Error('Usage: create-excellore-app <app-id> <local-port>');
}
const port = Number(portText);
if (port < 1024 || port > 65535) throw new Error('Invalid local port');
if (option && option !== '--refresh') throw new Error('Unknown option');
const target = resolve(root, 'apps', app);
if (target !== join(root, 'apps', app)) throw new Error('Invalid app path');
const origin = app === 'shell' ? `http://excellore.localhost:${port}` : `http://${app}.excellore.localhost:${port}`;
const replacements = {
  __APP_ID__: app,
  __APP_TITLE__: app[0].toUpperCase() + app.slice(1),
  __APP_PORT__: String(port),
  __APP_ORIGIN__: origin,
  __APP_SCOPES__: app === 'shell' ? 'openid profile email' : `openid profile email api-${app}`,
};
const template = resolve(root, 'packages/create-excellore-app/template');
await mkdir(target, { recursive: true });
async function copy(source, destination) {
  for (const entry of await readdir(source, { withFileTypes: true })) {
    const from = join(source, entry.name);
    const to = join(destination, entry.name);
    if (entry.isDirectory()) {
      await mkdir(to, { recursive: true });
      await copy(from, to);
    } else {
      const data = await readFile(from, 'utf8');
      const rendered = data.replace(/__APP_(?:ID|TITLE|PORT|ORIGIN|SCOPES)__/g, (key) => replacements[key]);
      await writeFile(to, rendered, { flag: option === '--refresh' ? 'w' : 'wx' });
    }
  }
}
await copy(template, target);
process.stdout.write(`Created apps/${app}. Configure its .env.local and run pnpm --filter @excellore/${app} dev.\n`);
