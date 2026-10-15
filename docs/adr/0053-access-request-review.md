# ADR-0053: Access request review boundary

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 requires role and app access requests. App enablement is a platform action, and the handbook does not define a role to grant from an app request. The owner chose to record app requests for platform review while tenant approval grants only role requests.

## Decision

Members create a request for an existing tenant-visible role or registered app using a machine-safe reason code and optional expiry. Tenant approvers may list and reject both kinds. Approval accepts only a role request: it rechecks the requester's active membership, the approver's current permissions, S1 anti-escalation, S2 self-approval, role availability, and step-up for privileged roles inside the same transaction that grants the assignment, marks the request approved, audits, and queues existing authorization and assignment events. App requests remain pending for a later platform review contract; tenant approval returns a conflict and changes no authorization data. An approver can reject an app request.

## Consequences

The platform review workflow must be defined before app requests can be fulfilled. There is no automatic app enablement or new permission. This security and public-contract decision needs owner review. INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, and INV-16 apply.
