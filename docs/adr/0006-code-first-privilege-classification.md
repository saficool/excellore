# ADR-0006: Classify privileged permissions from app catalogs

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

The handbook sections 7.7 and 20.2 require step-up for privileged permissions. The resolve response in section 7.8 returns permission keys but no privilege flags. Section 7.2 makes each app's `permissions.yaml` the code-first source for permission attributes. Adding privilege flags to the Access resolve response would change the internal contract. INV-08 requires the catalog to remain code-first; INV-10 requires unknown authorization state to deny access.

## Decision

The shared tenant gate asks an app-supplied `IPrivilegeClassifier` whether each required permission is privileged. Until an app registers its catalog classifier, the default treats every required permission as privileged. An app classifier must also treat unknown permission keys as privileged. The Access resolve response remains unchanged.

## Consequences

The gate requires ACR level 2 for unknown permissions, which may prompt more step-up than necessary while an app is being built. Catalog implementations and tests must prove that each `privileged: true` permission requires step-up. Review this decision before tenant endpoints ship in WP-05 and WP-06.
