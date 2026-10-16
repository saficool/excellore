import { readFileSync } from 'node:fs';
import {
  parseAppCatalog,
  parsePlatformStaff,
  type AppCatalog,
  type PlatformStaff,
} from './index.js';

export function loadAppCatalog(
  path: string,
  env: Record<string, string | undefined> = process.env,
): AppCatalog {
  return parseAppCatalog(readFileSync(path, 'utf8'), env);
}

export function loadPlatformStaff(path: string): PlatformStaff {
  return parsePlatformStaff(readFileSync(path, 'utf8'));
}
