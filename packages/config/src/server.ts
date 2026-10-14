import { readFileSync } from 'node:fs';
import { parseAppCatalog, type AppCatalog } from './index.js';

export function loadAppCatalog(
  path: string,
  env: Record<string, string | undefined> = process.env,
): AppCatalog {
  return parseAppCatalog(readFileSync(path, 'utf8'), env);
}
