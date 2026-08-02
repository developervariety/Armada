# Pipeline Test Guide

This guide verifies current persona and pipeline behavior. Run these tests on
a disposable vessel or a test tenant. Do not use a production repository for
failure-path tests.

## Automated Tests

Run:

```bash
dotnet build src/Armada.sln
dotnet run --project test/Armada.Test.Unit --framework net10.0
```

The suite must verify seed reconciliation, pipeline advancement, parallel
sibling behavior, persona prompt assembly, and MCP catalog coverage.

## Live Read-Only Checks

1. Call `armada_enumerate` for `personas`.
2. Confirm all 13 built-in personas are active.
3. Call `armada_enumerate` for `pipelines`.
4. Confirm all 13 built-in pipelines are active.
5. Read each pipeline and confirm every stage persona exists.
6. Run `armada_audit_operational_assets`.
7. Resolve the vessel workflow with `preview_workflow_profile`.

Treat audit errors as blockers. Review warnings before dispatch.

## Worker-Only Smoke Test

Dispatch a small, reversible documentation mission with `WorkerOnly`.

Verify:

- one Worker mission is created;
- the captain receives the expected prompt and active playbooks;
- the captain does not receive the Armada MCP catalog;
- `armada_mission_status` reports the correct persona and terminal state;
- `armada_voyage_status` reports the correct aggregate state.

## Sequential Pipeline Test

Dispatch a disposable change with `Tested`.

Verify this order:

1. Worker;
2. TestEngineer;
3. Judge.

The next stage must not start before all required missions in the prior order
succeed. Confirm that mission evidence and result markers pass to later stages.

## Parallel Sibling Test

Use a disposable custom pipeline with two personas at the same stage order, or
use the controlled reflection test when learned facts are enabled.

Verify:

- both sibling missions start at the same order;
- neither sibling waits for the other to start;
- the next order waits for both siblings;
- one required sibling failure prevents normal advancement.

Do not enable learned facts only to run a general pipeline test.

## Specialist Test

Select one specialist pipeline that matches the test repository. Confirm that
the specialist mission receives the Worker result and reviews only its stated
risk. Confirm that TestEngineer and Judge still run after the specialist.

## Default Resolution Test

On a disposable vessel:

1. Read fleet and vessel defaults.
2. Set a vessel default pipeline.
3. Dispatch without an explicit pipeline.
4. Confirm Armada selected the vessel default.
5. Restore the prior setting.

Repeat for the fleet default only when the test has an isolated fleet. Confirm
that `WorkerOnly` is the final fallback.

## Failure And Recovery Test

Use a mission designed to fail safely. Confirm:

- the pipeline does not advance incorrectly;
- the failure is visible in mission and voyage status;
- a retry or rescue creates durable evidence;
- the operator does not issue a duplicate dispatch;
- closeout does not claim success without passing Checks.

## Workflow Evidence

A successful pipeline is not enough for delivery. Run the resolved workflow
Checks and confirm the command, exit code, timestamps, and output are stored.
For deployment tests, also confirm approval, deploy, verification, monitoring,
rollback, and incident records as applicable.

## Pass Criteria

The test passes only when:

- personas and pipelines resolve from live records;
- stage order and parallel behavior are correct;
- captain prompts do not expose operator MCP tools;
- selected playbooks are active;
- required Checks contain real evidence;
- landing and closeout follow repository policy.
