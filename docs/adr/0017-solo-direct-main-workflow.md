# ADR-0017: Solo-maintainer direct commits to main

Status: Accepted by @saficool on 2026-10-14
Date: 2026-10-14
Owner: @saficool (platform and security owner while solo)
Review date: when another maintainer gains write access

## Context

The repository has one maintainer. ADR-0003 required a short-lived branch and pull request for every change, which adds a manual merge step without an independent reviewer. The owner requested direct pushes to `main` on 2026-10-14. This ADR is the short policy proposal and records that explicit owner decision under handbook section 37.1. It changes the repository review standard, not a platform invariant.

## Decision

While @saficool is the only maintainer, ordinary work-package steps may be committed and pushed directly to `main` in small, reviewable commits. Before pushing, the author reviews the full diff, runs the repository's build, lint, test, formatting, and secret checks, and records the reason, affected invariant IDs, verification, ADRs, follow-ups, and security review for security-owned paths in the commit body. This author review replaces the prior owner checklist in a PR while the repository is solo. A failed local check is fixed before pushing. The `javascript`, `dotnet`, `handbook`, and `secrets` CI jobs still run on `main`; a failed job is fixed immediately with a new commit. Force pushes and deletion of `main` remain prohibited.

Changes requiring independent approval, including production platform-staff assignments, still use a branch and pull request. If another maintainer gains write access, restore PR-based independent review for security-owned paths before that maintainer contributes. ADR-0003 is superseded for the solo workflow.

## Consequences

There is no pre-merge CI gate for ordinary direct commits. Local checks and post-push CI reduce risk but cannot provide independent review or prevent a bad commit from briefly reaching `main`. A bad commit is reverted or fixed with a new commit; history is never rewritten. The platform invariants and definition of done remain in force.
