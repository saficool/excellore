# Certificate expiry

1. Alert at 30, 14, and 7 days before expiry for public ingress and every private TLS endpoint.
2. Confirm the cloud certificate controller can renew through workload identity and that DNS
   validation records still exist.
3. Renew through the cloud adapter pipeline. Never upload a private key manually to a pod or GitHub
   log.
4. Verify the full chain, SANs, expiry, OCSP behavior, and TLS from an external probe. Then verify
   OIDC discovery and one login because issuer validation depends on identity TLS.
5. If expiry occurred, keep HTTP disabled, deploy the renewed certificate, restart only workloads
   that do not reload trust/certificates, and record outage duration.
