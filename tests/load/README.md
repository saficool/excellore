# WP-13 k6 load probes

`wp13.js` covers the four handbook load paths: login burst, authorization resolution, refresh storm, and bulk assignment. Run it only against an isolated local or staging tenant because the bulk scenario creates assignment jobs.

Provide `SHELL_URL`, `PROFILE_URL`, `ACCESS_API_URL`, `ACCESS_TOKEN`, `SESSION_COOKIE`, `USER_ID`, `TENANT_ID`, and `ROLE_ID`, then run:

```sh
docker run --rm -i --network host -e SHELL_URL -e PROFILE_URL -e ACCESS_API_URL -e ACCESS_TOKEN -e SESSION_COOKIE -e USER_ID -e TENANT_ID -e ROLE_ID -v "$PWD/tests/load:/tests:ro" grafana/k6:1.8.2 run /tests/wp13.js
```

The script never prints credentials. k6 validates the script in CI; the authenticated performance run belongs to the protected staging pipeline in WP-14.
