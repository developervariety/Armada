# Delivery Operations

This guide covers release, deployment, verification, rollback, incident, and
runbook operations. Use [armada-ops.md](armada-ops.md) for the complete
objective-to-closeout workflow and MCP catalog.

## 1. Record Ownership

| Record | Owns |
| --- | --- |
| Workflow profile | Repeatable build, test, package, deploy, verify, and rollback commands |
| Check | Command evidence and gate state |
| Environment | Rollout target and approval policy |
| Release | The work and artifacts that can ship |
| Deployment | Rollout, verification, monitoring, and rollback state |
| Incident | Impact, diagnosis, mitigation, and postmortem |
| Runbook execution | Evidence that an approved procedure ran |

Do not put this state only in a mission report or release note.

## 2. Prepare The Delivery Surface

Before a release or deployment:

1. Confirm the vessel working directory and default branch.
2. Confirm the workflow profile.
3. Confirm the target environment.
4. Confirm whether the environment requires approval.
5. Create Pending Checks for every required gate beyond build and unit test.
   Dispatch arms a voyage's Build and UnitTest Checks itself, so those two are
   already attached; add the rest.
6. Link the objective, voyage, missions, and Checks.

The profile should define the commands the delivery needs. Common fields are
build, unit test, integration test, package, release version, deploy, smoke
test, deployment verification, rollback, and rollback verification.

Pending Checks are requirements, not proof. They become proof only after a
real command result or valid external evidence resolves them. An armed Pending
Check is executed in place when the Judge stage reaches it, which is what lets
dispatch arm the build and unit-test gates without loading the host at the
moment the first captain starts working.

Use `list_workflow_profiles`, `get_workflow_profile`,
`validate_workflow_profile`, and `preview_workflow_profile` to inspect the
resolved command set. Use `create_workflow_profile` or
`update_workflow_profile` only with a complete profile object.

Use `list_environments` and `get_environment` to inspect the target. The words
Staging or Production do not prove that real deployment commands exist.

## 3. Create A Release

Use `create_release` when the work becomes a candidate for delivery.

Link:

- the vessel;
- the objective;
- the voyages and missions;
- required and completed Checks;
- artifacts and source commit;
- release notes that describe the delivered behavior.

Read the release with `get_release`. Use `update_release` to correct links,
notes, version, or state. `armada_update_release` is a compatibility alias.

Do not mark a release as shipped until its required evidence is complete.

## 4. Create And Approve A Deployment

Use `create_deployment` with a named environment and a source release or ref.
Read the result with `get_deployment`.

If the environment requires approval, the deployment waits for
`approve_deployment`. Approval is an outward action. The operator must review
the target, source, profile, and required Checks before approval.

Use `update_deployment` to correct deployment metadata before execution.
`armada_update_deployment` is a compatibility alias.

## 5. Verify A Deployment

Use `verify_deployment` to run the configured verification path. Review:

- deploy command result;
- smoke test;
- health check;
- deployment verification;
- monitoring window and summary;
- linked incidents.

Deployment actions use matching Pending deployment Checks when they exist. If
no matching Check exists, Armada creates one so the evidence is durable.

A successful deploy command does not prove that the rollout is healthy. The
verification command or an equivalent state query must prove the result.

## 6. Roll Back

Use `rollback_deployment` on the existing deployment record. Do not create a
second deployment only to represent the rollback.

Before rollback:

1. Confirm the exact deployment and environment.
2. Read the failure evidence.
3. Confirm the rollback source or procedure.
4. Open or update the related incident.

After rollback:

1. Read rollback and rollback-verification Checks.
2. Confirm the deployment state.
3. Confirm the actual target state.
4. Link the evidence to the incident and objective.

## 7. Incidents

Use `armada_create_incident` when a delivery failure has operational impact.
Use `armada_get_incident` and `armada_update_incident` during diagnosis and
recovery.

Record:

- impact;
- affected environment, deployment, release, vessel, voyage, and mission;
- failing Check;
- root cause when known;
- recovery and rollback evidence;
- follow-up objective;
- postmortem.

Use `armada_close_incident` only when linked evidence supports closure. Do not
close an incident only because a captain or operator says that the work is
done.

When incident lifecycle automation is enabled, newer passing Checks,
successful rescue missions, shipped releases, verified deployments, and
completed rollbacks can mitigate or close linked incidents after the quiet
period. A newer matching failure can reopen an incident.

## 8. Runbooks

Use `list_runbooks` and `get_runbook` to inspect the approved procedure. Use
`start_runbook_execution` when the procedure runs. Record completed step IDs,
notes, and status with `update_runbook_execution`. Read the final evidence with
`get_runbook_execution`. Use `list_runbook_executions` before you start a
duplicate execution.

Use `create_runbook` and `update_runbook` to manage procedures. Use
`delete_runbook` and `delete_runbook_execution` only with explicit destructive
authority.

Good runbook subjects include:

- release readiness;
- deployment approval;
- deployment verification;
- rollback;
- migration;
- incident response;
- Admiral deployment and recovery.

The execution record must show what ran and what result it produced.

The server-owned mission-recovery runbook is part of autonomous recovery. Do
not replace it with a general human checklist.

## 9. Checks And Automatic Resolution

Use `get_check_run` to read one Check. Use `run_check` for initial execution and
`retry_check_run` for a real rerun. Use `resolve_check` only for valid evidence
that was produced outside Armada.

These three return a BOUNDED view: status, exit code, parsed test and coverage
totals, artifacts, and the last lines of output. They do not return the whole
command log, which for a real vessel runs to megabytes and overruns the tool
output limit. Pass `includeOutput=true` to `get_check_run` for the complete
record, or `outputTailLines` to widen the tail. A truncated view states the
full log's size and names the call that fetches it, so nothing is withheld
silently. The REST check endpoints are unchanged and still return the full
record.

An expensive Check does not run concurrently with a definition-of-done gate or
a merge-queue test run; Armada serializes them host-wide. A Check submitted
during a gate queues rather than racing, so it can take considerably longer to
return than the command itself takes.

When automatic Check resolution is enabled, the heartbeat can run eligible
non-deployment Checks after linked missions or voyages complete, when a release
is ready, or when an idle vessel needs a baseline check. Deployment deploy,
verify, and rollback Checks stay under the deployment action so the deployment
record remains authoritative.

## 10. Minimum Closeout

Before you report delivery completion:

- the release links the correct work and source commit;
- required Checks passed;
- the deployment targets the correct environment;
- approval exists when required;
- verification proves the live state;
- rollback evidence exists if rollback occurred;
- incidents match their evidence;
- follow-up work has a separate objective or backlog item;
- the parent objective contains the final links and verified outcome.

## 11. Current Boundaries

Armada's internal records are authoritative for its own workflow. External CI,
provider review, and deployment systems can require separate evidence links.
Do not report external state as verified until Armada has ingested it or the
operator has checked it directly.
