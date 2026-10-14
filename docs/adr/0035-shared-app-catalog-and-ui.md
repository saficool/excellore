# ADR-0035: Shared app catalog and header

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-04 requires shared UI chrome, design tokens, and configuration that lets the Shell and other apps navigate tenants and apps. The handbook gives example production URLs, but deployment domains are not yet supplied. Its app catalog is configuration as code. Browser links must not become authorization authority (INV-02), and app URLs must not accept unsafe schemes or unexpected origins.

## Decision

The versioned YAML catalog records app identity, display information, order, core status, app access permission, and an environment variable name for each public origin. The shared config package resolves origins at build or deploy time, requires HTTPS except for explicitly flagged local development on localhost, validates tenant slugs, rejects duplicate app IDs and mismatched app permissions, and returns only links present in both the Access snapshot's app and permission lists. The existing local PROFILE_ORIGIN and ACCESS_ORIGIN variables provide development values; production values come from deployment configuration.

The shared UI package exports a server-renderable React header and CSS design tokens. The header presents the current tenant, tenant switcher, app switcher, account link, and POST sign-out form. It uses native disclosure controls and accepts externalized labels. The caller supplies only the tenant and app links returned by the Access-backed config helper; the BFF and API still enforce authorization independently.

## Consequences

INV-02, INV-10, and INV-11 remain intact. The catalog is source controlled without a deployment hostname. WP-04's app template and WP-07's Shell must read the catalog on the server and pass authorized links to the header. Security review is required before accepting this ADR. New dependencies are pinned: React and React DOM 19.3.0 and their type packages (MIT), yaml 2.9.1 (ISC), and the existing Zod 4.6.7 (MIT).
