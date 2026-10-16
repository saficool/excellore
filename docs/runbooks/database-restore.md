# Database restore

## Full database restore

1. Declare an incident, stop writes for the affected service, and preserve logs and provider audit
   records without copying personal data into the incident ticket.
2. Select a restore point before the fault and within the five-minute RPO. Record timestamps and
   identifiers in the restricted incident record.
3. Use the cloud adapter pipeline to restore the managed database to a new private endpoint. Never
   overwrite the affected database in place.
4. Run migrations in verification mode, forced-RLS verification, audit-chain verification, row
   counts, and application smoke tests against the restored endpoint.
5. Change the service endpoint through the protected deployment pipeline. Monitor readiness,
   authorization denials, outbox age, and errors.
6. Keep the old database isolated until the incident owner approves disposal under the future
   retention policy. Record achieved RPO and RTO.

## Single-tenant restore

1. Suspend the tenant through Access and confirm all app writes fail closed.
2. Restore the complete affected service database to a private scratch instance at the chosen PITR
   point. Deny application and public network access to the scratch instance.
3. Provision a time-limited restore role on the target. It must have only the required active-table
   `SELECT`, `DELETE`, and `INSERT` grants; it must not be superuser, `BYPASSRLS`, a table owner, or
   a member of an owning role.
4. Create a mode-0700 work directory on an encrypted temporary volume. Set database URLs only in
   the process environment, confirm the encrypted volume through the script's explicit safety
   variable, then run `scripts/operations/single-tenant-restore.sh` once for Access and once for
   Profile.
5. The script checks that scratch and target are different, verifies the target role, applies
   transaction-local tenant context, checks file hashes, replaces active tenant rows in dependency
   order, and leaves audit archives, tombstones, global identities, inbox, and outbox untouched.
6. While the tenant remains suspended, verify membership/role counts, app gates, Profile directory
   rows, cross-tenant isolation, forced RLS, and audit chains. Confirm no new event was published for
   restored historical changes.
7. Remove the restore role, securely destroy temporary files and the scratch instance, then
   reactivate the tenant through Access after owner approval.

Abort on any checksum, RLS, foreign-key, identity, or audit discrepancy. Restore to a new scratch
instance and investigate; never weaken a constraint or trigger to make the restore pass.
