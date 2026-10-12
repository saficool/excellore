# ADR-0003: Solo-maintainer repository review

Status: Accepted by @saficool on 2026-10-12; effective when the accompanying handbook change merges.
Date: 2026-10-12
Owner: @saficool (platform and security owner while solo)
Review date: 2027-01-12, or when another maintainer gains write access, whichever comes first.

## Context

Excellore currently has one developer, who is also the named platform security owner in `CODEOWNERS`. GitHub does not allow pull-request authors to approve their own PRs. The handbook previously required an independent security-owner review for several paths and a reviewer for documentation changes. Those requirements cannot be met in a solo repository. The owner explicitly decided that no separate reviewer is needed while working alone. This changes a mandatory repository standard under handbook section 37.1; no platform invariant changes.

## Decision

The repository owner may merge their own PRs without a GitHub approving review while they are the only maintainer, after these checks:

1. Work happens on a short-lived branch and enters `main` through a PR.
2. The PR description records the reason, affected invariant IDs, tests, ADRs, and any follow-up. The owner reviews the full diff and records the review using the PR template.
3. For security-owned paths, the owner records the authentication, authorization, tenant-isolation, secret-handling, and audit implications. Security decisions receive an ADR. Failed checks are fixed, not bypassed.
4. The final PR commit passes the `javascript`, `dotnet`, `handbook`, and `secrets` CI jobs. `main` is protected against force pushes and deletion, requires a PR and these checks, and allows zero bypasses. The GitHub ruleset requires zero approving reviews while the repository is solo.
5. `CODEOWNERS` continues to identify @saficool as the security owner. When another maintainer with write access joins, revisit this decision and require independent code-owner approval for security-owned paths.

## Consequences

The owner accepts the loss of independent review while solo. The PR record, mandatory CI, and protected branch provide an auditable process, but they cannot replace a second person's judgment. The policy does not relax platform invariants, tenant-isolation tests, secret scanning, or the definition of done. Repository settings must match the ruleset documented in `docs/runbooks/github-branch-protection.md`.
