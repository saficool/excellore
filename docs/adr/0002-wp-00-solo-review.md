# ADR-0002: Solo-maintainer review for WP-00

Status: Proposed — requires explicit approval by the repository owner before PR #1 is merged.
Date: 2026-10-12
Owner: @saficool (decision pending)
Expiry / review date: On merge of WP-00, or 2026-11-12, whichever comes first.

## Context

Excellore is currently a solo repository. The handbook requires security-owner review for changes under `services/access/` and shared security libraries (sections 0.7 and 24.2). WP-00 creates empty scaffolds in those paths. PR #1 is authored by the only named code owner, @saficool. GitHub does not allow an author to approve their own pull request. The required independent review therefore cannot occur for this bootstrap PR.

This is a proposed deviation from the handbook's review rule, not a change to any platform invariant. It needs a human decision under sections 0.5 and 37.1. INV-11 and INV-14 are touched by the bootstrap's secret scanning and CI configuration.

## Proposed decision

For PR #1 only, the repository owner may substitute a documented manual review for an independent security-owner approval after all of the following are true:

1. The PR description names the affected invariants and links this ADR.
2. The owner reviews the complete diff and records a PR comment confirming that no authentication, authorization, tenant-scoped route, permission, database table, secret, or production configuration is implemented in WP-00.
3. The owner confirms that the JavaScript, .NET, handbook-sync, and secret-scan CI jobs pass on the final PR commit.
4. The owner explicitly approves this one-time deviation in a PR comment before merging.

The exception ends when PR #1 is merged or on the review date above. It does not apply to WP-01 or later changes. The handbook's security-owner review rule remains in force for subsequent PRs; an independent reviewer must be appointed before merging later changes to protected paths.

## Consequences

WP-00 can be merged without pretending that a self-review is an independent approval. The decision and its limits remain visible in the PR history. Independent review is still unavailable during bootstrap, so the owner must accept that residual risk explicitly. If the proposed exception is declined, PR #1 waits for an independent security reviewer.
