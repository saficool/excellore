# ADR-0070: Personal export audit boundary (proposed)

## Context

WP-12 requires a complete personal-data export across Excellore services.
Restricted, hash-chained audit originals can identify the requester, actors,
targets, and other people in the same record. Returning those originals would
disclose other people's data; rewriting them for export would require a legal
redaction policy that the handbook's Q-11 retention and compliance decision
does not yet define.

## Decision

Personal-data exports include the requester's live global Profile data,
preferences, tenant memberships and profiles, and other live application rows
linked to the requester. They exclude restricted audit originals and archives.
The application runtime receives no new read grant on restricted archives.

## Consequences

The export is complete for live product data but does not claim to satisfy an
undefined legal right of access to security audit records. A future compliance
decision may add a separately reviewed, redacted disclosure workflow. Audit
records remain append-only and available only through the restricted security
path.

INV-03, INV-11, INV-12, INV-18.
