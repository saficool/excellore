# Excellore

The platform handbook is the [single source of truth](excellore-platform-handbook.md). This repository is currently at **WP-00: repository bootstrap**. The app and service projects are scaffolds; they do not yet implement authentication, authorization, tenant APIs, or business behavior.

## Prerequisites

- Node.js 22.13 or newer, pnpm, and .NET SDK 10. The repository pins pnpm 10.34.6; newer pnpm versions switch to it when run here.
- Gitleaks for the local pre-commit secret scan
- Git, initialized manually by the repository owner

On Windows, install Gitleaks from the WinGet catalog in a normal PowerShell terminal:

```powershell
winget install --id Gitleaks.Gitleaks --exact
gitleaks version
```

If WinGet is unavailable, download the Windows binary from the official [Gitleaks releases](https://github.com/gitleaks/gitleaks/releases), extract it into a directory on your `PATH`, then run `gitleaks version` in a new terminal.

## First-time setup

```sh
pnpm install --frozen-lockfile
sh scripts/install-hooks.sh
```

## Checks

```sh
pnpm -w build
pnpm -w lint
pnpm -w test
pnpm -w format:check
dotnet build Excellore.sln
dotnet test Excellore.sln
```

CI runs these checks and Gitleaks on pushes to `main` and pull requests. Security-sensitive paths are assigned to `@saficool` in `.github/CODEOWNERS`.

The handbook is maintained at the repository root. Run `sh scripts/sync-handbook.sh` after changing it; CI checks that `docs/handbook.md` is identical.
