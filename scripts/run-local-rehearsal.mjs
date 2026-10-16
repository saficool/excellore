import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';

const candidates =
  process.platform === 'win32'
    ? [
        join(
          process.env.ProgramFiles ?? 'C:\\Program Files',
          'Git',
          'bin',
          'bash.exe',
        ),
        join(
          process.env.ProgramFiles ?? 'C:\\Program Files',
          'Git',
          'usr',
          'bin',
          'bash.exe',
        ),
      ]
    : ['bash'];
const bash = candidates.find(
  (candidate) => candidate === 'bash' || existsSync(candidate),
);

if (!bash) {
  console.error(
    'Git Bash is required on Windows to run the local WP-14 rehearsal.',
  );
  process.exit(1);
}

const result = spawnSync(
  bash,
  ['scripts/operations/rehearse-local-docker.sh'],
  {
    cwd: process.cwd(),
    env: process.env,
    stdio: 'inherit',
  },
);

if (result.error) throw result.error;
process.exit(result.status ?? 1);
