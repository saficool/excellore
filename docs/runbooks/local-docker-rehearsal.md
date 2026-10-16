# Local Docker production rehearsal

## Purpose

Exercise WP-14 recovery mechanics while cloud selection is deferred. This rehearsal validates the
portable parts of backup restore, forced RLS, credential rotation, and network exposure. It does
not prove managed PostgreSQL PITR, cloud workload identity, DNS/TLS, regional recovery, or a
pipeline-only staging deployment.

## Run

1. Start the complete development stack with `scripts/dev-up.sh`.
2. Apply the Access and Profile migrations by starting their APIs at least once.
3. Run `pnpm -w wp14:rehearse-local` from the repository root.
4. Review `.local/wp14-local-rehearsal.json`. It contains timings and pass/fail metadata only.

The script performs these checks without changing current application credentials or writing a
database dump to the host:

- every published Compose port is bound to loopback;
- Keycloak, Access, and Profile are streamed into disposable scratch databases;
- each restored schema matches its source exactly;
- restored Access and Profile tenant tables still use forced RLS;
- a disposable, unprivileged database role accepts a new random credential and rejects its old
  credential after rotation;
- all scratch databases, credentials, and the probe role are removed on success or failure.

The evidence explicitly records `satisfiesManagedCloudAcceptance: false`. Run the staging drills
from [Production deployment](production-deployment.md) after a provider and region are approved.
