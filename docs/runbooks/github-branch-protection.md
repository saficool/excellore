# GitHub branch protection for a solo repository

Owner: @saficool
Decision: ADR-0003 and handbook section 24.2

The repository owner applies this ruleset in GitHub under **Settings → Rulesets → Rulesets → New ruleset → New branch ruleset**. Target only the default branch, `main`, and set enforcement to **Active**. See [GitHub's ruleset instructions](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/creating-rulesets-for-a-repository).

- Require a pull request before merging, with **0 required approvals** while the repository has one maintainer.
- Require status checks to pass before merging: `javascript`, `dotnet`, `handbook`, and `secrets` from the `CI` workflow. Select checks from a completed run; do not type unchecked lookalike names.
- Require the branch to be up to date before merging when GitHub offers the option.
- Select **Block force pushes** and **Restrict deletions** for `main`.
- Do not grant a bypass actor for the pull-request or status-check rules.

After saving, verify GitHub reports `main` as protected. Open a small test PR or inspect the next PR to confirm all four jobs appear as required checks and merge is blocked until they pass. When another maintainer gains write access, revisit ADR-0003 and enable independent code-owner approval for security-owned paths.
