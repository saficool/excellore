# ADR-0073: Application user erasure orchestration (proposed)

## Context

ADR-0068 permits application data erasure while Keycloak identity deletion is
deferred under INV-11. The implementation still needs to deny authorization
immediately, remove personal application data across Access and Profile, retain
append-only audit originals, survive duplicate event delivery, and prevent an
active Keycloak session from recreating erased data.

## Decision

Profile creates an idempotent deletion request after recent MFA and publishes
`profile.deletion.requested.v1`. Access handles it first. Under the approved
`access_worker` grants, Access marks its identity mirror `deleted`, clears the
email and name fields, removes memberships and active grants in each explicit
tenant context, and replaces retained live actor references with a random opaque
tombstone ID. It leaves restricted audit rows unchanged and appends one
machine-attributed erasure marker per affected tenant. The sanitized identity
row and class-P request receipt prevent a still-active Keycloak identity from
being mirrored back into active application state.

Access publishes the existing ID-only `access.erasure.confirmed.v1` with user
scope. Profile then discovers only that user's tenant profile rows through its
existing user-bounded policy, deletes them one tenant context at a time, removes
preferences, avatar metadata and objects, and private export jobs and objects.
It scrubs the retained `user_profiles` row, marks it deleted, appends a restricted
audit marker, records an opaque app tombstone, and publishes the existing
user-scoped `profile.erasure.confirmed.v1`. Access records Profile's receipt.

Both services expose the terminal application state as
`pending_external_identity_deletion`. They do not call the Keycloak Admin API
and do not publish `identity.user.deleted.v1`.

## Consequences

Application access and personal data are removed without claiming that the
global identity has been deleted. Duplicate delivery is harmless and tested.
Restricted audit originals remain unchanged. The opaque Keycloak subject remains
only in sanitized application tombstone rows until a separately approved
identity-provider deletion design completes the external step.

INV-02, INV-03, INV-07, INV-10, INV-11, INV-12, INV-13, INV-16.
