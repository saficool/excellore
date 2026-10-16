import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const path = process.argv[2] ?? 'report_json.json';
const report = JSON.parse(await readFile(path, 'utf8'));
assert.ok(Array.isArray(report.site), 'ZAP report must contain scanned sites');
const high = report.site
  .flatMap((site) => site.alerts ?? [])
  .filter((alert) => Number(alert.riskcode) >= 3)
  .map((alert) => `${alert.pluginid}: ${alert.name}`);
assert.deepEqual(high, [], `High-severity ZAP findings:\n${high.join('\n')}`);
process.stdout.write('OWASP ZAP reported no high-severity findings.\n');
