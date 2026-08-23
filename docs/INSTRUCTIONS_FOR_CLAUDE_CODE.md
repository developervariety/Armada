# Armada Instructions For Claude Code

Use Armada as the structured work and delivery system. You are the
orchestrator. Captains perform mission work in isolated docks.

## Required Reading

Read `docs/armada-ops.md` before you operate Armada. It contains the complete
workflow and all registered MCP tool names. Read `docs/MCP_API.md` for live
schema discovery and transport behavior.

## Session Start

1. Call `armada_status`.
2. Enumerate active voyages, missions, captains, merge entries, incidents,
   objectives, and Checks with small pages.
3. Drain the audit queue.
4. Read each relevant open objective in full.
5. Check incidents and the merge queue before new dispatches.

## Work Intake

Use an objective or backlog item for non-trivial work. Put scope, acceptance
criteria, constraints, and deferred work in that record.

Use backlog refinement when intent is unclear and repository context is not
needed. Use a planning session when the work is tied to a vessel.

Inspect the repository and git history before dispatch. Confirm that the work
is not already present.

## Dispatch

Use `armada_dispatch` for voyage work. Include the objective ID, vessel,
mission mode, configured pipeline, exact scope, exclusions, and verification.

Select `preferredModel` as `low`, `mid`, or `high`. Do not choose a
concrete provider model for ordinary dispatch.

Use `Implementation` mode when a commit is required. Use `Audit` or
`Research` for report-only work.

A successful dispatch response means that records exist. Captain assignment
and dock launch continue in the background. Save the voyage ID and monitor it.
Do not redispatch only because the first state is `Pending`.

## Monitor

Keep an active loop while work runs.

- Read voyage and mission status.
- Read mission and captain logs when progress is unclear.
- Use captain diagnostics before deep process inspection.
- Poll incidents and Checks.
- Nudge only live work that needs missing context.
- Do not steer a terminal mission.

Treat captain success text as unverified. Run the check or query that proves
the result.

## Checks And Landing

Dispatch arms a voyage's Build and UnitTest Checks itself, so those two are
already attached. Add the profile gates the change needs beyond them. Build and
unit test remain the minimum for code work; you no longer have to create them,
but you do have to confirm they are present when a voyage was built some other
way.

`run_check`, `retry_check_run`, and `get_check_run` return a bounded summary -
status, exit code, parsed test totals, artifacts, and the tail of the output -
not the whole command log. Pass `includeOutput=true` to `get_check_run` when
you need the complete log.

Review the diff, logs, audit state, and Checks before landing. Process the exact
merge entry. Verify the target branch after landing.

Do not use `resolve_check` to hide a failure. It is only for valid evidence
that was produced outside Armada.

## Closeout

Before an objective becomes complete:

- link final voyages and missions;
- link passing Checks;
- link releases and deployments when work shipped;
- link incidents and final evidence;
- create a separate record for deferred work;
- write the verified outcome in the objective.

## Safety

Read a record before you change it. Confirm exact IDs before cancel, delete,
purge, restore, rollback, or server-stop actions. Do not expose credentials or
private operational identifiers in public artifacts.

Tool names can have a client-specific server prefix. Use the live tool
description and input schema. Follow every `nextCursor` returned by
`tools/list`.
