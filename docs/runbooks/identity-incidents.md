# Identity incident runbook

## Keycloak outage

1. Confirm both Keycloak pods, management readiness, database health, and identity proxy health.
2. Freeze deployments and realm reconciliation. Existing API access may continue only while valid
   tokens and cached keys remain usable; new login, refresh, logout, and step-up will fail.
3. Restore the database or roll back the Keycloak image through the pipeline as applicable.
4. Verify OIDC discovery, one BFF login, MFA step-up, service-account tokens, back-channel logout,
   and both Keycloak nodes before resolving the incident.

## Signing-key compromise

1. Disable public login at the identity edge and preserve Keycloak/provider audit evidence.
2. Through a reviewed realm-config change, create a new signing key, make it active, retire the
   compromised key, and advance realm not-before to invalidate existing tokens.
3. Redeploy or restart token-verifying services only if JWKS refresh does not converge.
4. Verify the old key and old tokens are rejected, then reopen login. Record the key ID only; never
   record key material or tokens.

## Client-secret leak

1. Identify the client without copying the secret. Disable the client or close its network path.
2. Generate a replacement in the secret manager, update Keycloak through realm reconciliation, and
   let External Secrets refresh the single affected workload Secret.
3. Roll the affected workload through the pipeline and prove the previous secret fails.
4. For `svc-access-admin`, also inspect identity-admin audit events and invitation activity, revoke
   suspicious sessions, and keep onboarding paused until the security owner closes the incident.

## Mass session revocation

Advance realm not-before and revoke offline sessions through a reviewed Keycloak realm operation.
Verify BFF sessions can no longer refresh and force each BFF session namespace to a new key prefix.
Do not enumerate user IDs in URLs or logs.

## Compromised platform administrator or tenant owner

Remove platform roles through the code-owned staff roster or suspend affected tenant memberships
through Access. Revoke all sessions using the realm-wide procedure when impact is uncertain. The
individual Keycloak disable/sign-out API remains deferred by the approved WP-11 decision because
it requires a user identifier in the Keycloak Admin API URL; do not bypass that decision manually.
