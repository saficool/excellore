# Production runbooks

| Handbook scenario                                                                                                                                  | Runbook                                                                           |
| -------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| Keycloak outage, signing key compromise, client secret leak, `svc-access-admin` abuse, mass session revocation, compromised administrator or owner | [Identity incidents](identity-incidents.md)                                       |
| Access authority outage, suspected cross-tenant leak, emergency tenant suspension                                                                  | [Authorization and isolation incidents](authorization-and-isolation-incidents.md) |
| Dead-letter growth, Valkey outage, RabbitMQ outage                                                                                                 | [Messaging and cache incidents](messaging-and-cache-incidents.md)                 |
| Full and single-tenant database restore                                                                                                            | [Database restore](database-restore.md)                                           |
| Certificate expiry                                                                                                                                 | [Certificate expiry](certificate-expiry.md)                                       |
| Application onboarding                                                                                                                             | [Onboard application](onboard-application.md)                                     |
| Deployment, rollback, restore and rotation drills                                                                                                  | [Production deployment](production-deployment.md)                                 |
| Local backup, restore, RLS, port-boundary, and credential-rotation rehearsal                                                                       | [Local Docker production rehearsal](local-docker-rehearsal.md)                    |

For every incident, assign an incident commander, preserve restricted evidence, and record only
opaque resource IDs and trace IDs in general tooling. Hold the post-incident review within five
working days. Record timeline, root cause, corrective actions, and required handbook or ADR changes.
Suspected exposure follows the legal and customer-notification process; the runbook does not decide
notification or retention obligations.
