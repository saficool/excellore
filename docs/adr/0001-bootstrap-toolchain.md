# ADR-0001: Bootstrap toolchain

Status: Proposed

## Context

The handbook specifies pnpm, Turborepo, TypeScript, ESLint, and .NET, but does not pin versions. WP-00 needs repeatable local and CI commands.

## Decision

Use Node.js 22, pnpm 10.34.6, Turborepo 2.11.2, TypeScript 5.9.3, ESLint 10.10.0, and .NET 10. The apps and packages start as strict TypeScript scaffolds. Service and shared-library projects start as .NET scaffolds. No authentication, authorization, or tenant route is implemented in this package.

## Consequences

CI and local development require these toolchains. The platform team should confirm the versions before feature work starts. A later package may add its required application dependencies and tests without changing the repository layout.
