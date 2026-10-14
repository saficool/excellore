# GitHub main protection for a solo repository

Owner: @saficool
Decision: ADR-0017 and handbook section 24.2

While the repository has one maintainer, ordinary changes may be pushed directly to `main` after the local checks and review record required by ADR-0017. GitHub Actions runs `javascript`, `dotnet`, `handbook`, and `secrets` on every push to `main`. Check their result after each push and fix failures immediately with a new commit.

If a GitHub ruleset currently requires a pull request or pre-push status checks, @saficool must update it in **Settings → Rules → Rulesets** to allow direct pushes. Keep force pushes and branch deletion blocked. Do not grant an actor permission to bypass those protections. Use a PR and record independent approval for exceptional changes even though the ordinary direct-push ruleset cannot enforce it selectively.

When another maintainer gains write access, revisit ADR-0017 and restore independent review for security-owned paths before that maintainer contributes.
