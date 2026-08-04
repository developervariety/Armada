# Armada Operator Guide

This guide is the canonical operating procedure for Armada. It describes how
an orchestrator creates, monitors, verifies, lands, and closes work. It also
lists every MCP tool that the server registers.

Use [MCP_API.md](MCP_API.md) for transport and schema discovery. Use
[DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and deployment
detail. Use [MERGING.md](MERGING.md), [PIPELINES.md](PIPELINES.md), and
[SCHEDULING.md](SCHEDULING.md) for subsystem detail. Use
[OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md) for playbooks, runbooks,
workflow profiles, environments, personas, pipelines, and their links.

## 1. Source Of Truth

Armada records are the source of truth for work and delivery state.

| Record | Owns |
| --- | --- |
| Objective or backlog item | Scope, acceptance criteria, priority, constraints, and deferred work |
| Planning or refinement session | Decisions made while the scope is not ready for dispatch |
| Voyage and mission | Work assignment and captain execution |
| Check | Command evidence and gates |
| Merge entry | Review, integration, test, and landing state |
| Release | A candidate or shipped unit |
| Deployment | Rollout, approval, verification, and rollback |
| Incident | Failure impact, diagnosis, mitigation, and closure evidence |
| Runbook execution | Evidence that a repeatable procedure ran |
| Event, signal, and request history | Timeline and communication evidence |

Do not use mission prose as a replacement for these records. Do not treat a
captain report as proof. Run the command or query that proves the result.

## 2. Available Features And Active Policy

The repository contains features that an operator can disable. Documentation
of a feature does not mean that a deployment enables it.

Check these settings before you depend on the related workflow:

| Setting or record | Effect |
| --- | --- |
| `codeIndex.enabled` | Enables code search, graph search, and context packs. |
| `learnedFactsEnabled` | Enables learned-playbook injection and reflection workflows. |
| `seedDockRuntimeMcpConfig` | Gives compatible captains dock-local Armada MCP configuration. |
| `autonomousRecovery.enabled` | Enables bounded server-side mission recovery. |
| `incidentLifecycle.enabled` | Enables evidence-driven incident transitions. |
| Objective `AutoDispatchEnabled` and scheduler state | Enables autonomous objective dispatch. |
| Vessel or voyage landing mode | Selects `LocalMerge`, `PullRequest`, `MergeQueue`, or `None`. |
| Vessel default pipeline | Selects the normal persona path. |
| Workflow profile | Defines the commands that Checks and delivery operations run. |

When a feature is off, use the explicit fallback. For example, search the
checkout directly when code indexing is off. Do not call disabled tools in a
loop.

## 3. Connection And Discovery

The Admiral exposes stateless Streamable HTTP MCP at `/mcp`. `/rpc` is a
compatibility alias. An SSH stdio bridge can forward a local MCP client to a
loopback-bound remote Admiral. The bridge must connect to the running Admiral.
It must not start a second embedded Admiral process.

Start each operator session with:

1. Call `armada_status`.
2. Call `armada_enumerate` with small pages for active voyages, missions,
   captains, merge entries, incidents, objectives, and checks.
3. Call `armada_drain_audit_queue` before new dispatches.
4. Read each relevant open objective in full.
5. Check incidents and the merge queue before you create more work.
6. Check `armada_unlanded_branches` when prior work can exist outside the
   normal landing path.

MCP `tools/list` is paginated. Follow `nextCursor` until it is absent. The
normal built-in catalog fits on one 500-tool page. Pagination remains active
for larger extension catalogs. A client that ignores `nextCursor` can hide
valid tools.

The Armada MCP catalog is an operator surface. Do not deliver it to captains.
It contains dispatch, administration, deployment, restore, purge, and server
control actions. Captains work inside their dock with the runtime tools that
their mission needs. An operator uses MCP to create and control that work.

`armada_enumerate` supports these entity types:

`fleets`, `vessels`, `captains`, `missions`, `voyages`, `docks`, `signals`,
`events`, `merge_queue`, `personas`, `prompt_templates`, `pipelines`,
`playbooks`, `objectives`, `incidents`, `checks`, `releases`, and
`deployments`.

Use `pageSize` from 10 to 25 unless a larger page is necessary. Large text
fields are excluded by default. Request `includeDescription`, `includeContext`,
`includeTestOutput`, `includePayload`, or `includeMessage` only when needed.

## 4. Standard Workflow

### 4.1 Capture The Work

For non-trivial work, find or create an objective before dispatch.

1. Search with `list_objectives`, `list_backlog`, or
   `armada_enumerate(entityType: "objectives")`.
2. Read the selected record with `get_objective` or `get_backlog_item`.
3. Add clear acceptance criteria and verification requirements.
4. Put deferred work in a separate objective or backlog item. Do not bury it
   in closed-record prose.

Use backlog refinement when intent is still unclear and repository context is
not yet needed. Use a planning session when the work is tied to a vessel and
must become dispatch-ready.

### 4.2 Inspect Before Dispatch

Read the target repository rules and inspect the current code and git history.
Confirm that the requested work is not already present. Confirm the vessel,
pipeline, model tier, landing mode, protected paths, and workflow profile.

If code indexing is enabled, use `armada_index_status` before index-dependent
work. If it is disabled, use checkout search and set `codeContextMode` to
`off`.

### 4.3 Select Mission Shape

Use mission mode `Implementation` for work that must produce a commit. Use
`Audit` or `Research` for report-only work. Read-only modes do not require a
commit and must not receive implementation-only instructions.

Use the vessel's configured pipeline unless the approved work calls for a
different existing pipeline. Use the full configured persona path. Do not
remove review stages only to make a voyage faster.

Dispatch with `preferredModel: "low"`, `"mid"`, or `"high"`. Do not put a
concrete provider model in an ordinary mission brief.

### 4.4 Dispatch

Use `armada_dispatch` for a voyage. Put all ordered missions for one vessel in
one request. Use aliases and dependency aliases when order matters.

Include:

- `objectiveId` for durable work;
- the vessel ID;
- the pipeline when the vessel default is not correct;
- one mission title and complete description per unit of work;
- mission mode;
- model tier;
- exact scope and exclusions;
- required verification;
- only the prestaged files that downstream stages can read and need.

`armada_dispatch` is durable-first. A successful response means that the
voyage and mission rows exist. Assignment, dock provisioning, and captain
launch continue asynchronously. Save the voyage ID. Do not redispatch only
because the first status call shows `Pending`.

Long operations can return an accepted job. Poll `armada_job_status` with the
returned job ID.

### 4.5 Monitor

Dispatch is the start of the operator loop.

1. Poll `armada_voyage_status` in summary mode.
2. Read `armada_mission_status` for the active or failed stage.
3. Read mission and captain logs when progress is unclear.
4. Use `armada_captain_diagnostics` before deep process inspection.
5. Poll incidents and Checks on the same cadence.
6. Use `armada_nudge_voyage` or `armada_send_signal` only for live work that
   needs missing context.
7. Do not steer a terminal mission. Use restart, recovery, or a new mission.

A quiet captain is not proof of a stall. Compare the mission state, process
ID, dock status, log activity, and elapsed time.

### 4.6 Verify With Checks

Create Pending Checks when the objective or voyage is created. Build and unit
test are the minimum for code changes. Add the vessel-profile gates that the
change needs.

Use `run_check` to execute a check. Use `retry_check_run` for a real rerun.
Use `resolve_check` only when valid evidence was produced outside Armada. Do
not use it to hide a failure.

A passing suite proves only that the suite passed. It proves a fix only when
the check covers the original symptom. Record before and after evidence when
the task is a defect.

### 4.7 Review And Land

Read the mission diff and relevant logs. Drain the audit queue and record the
audit verdict when needed. Check the merge entry before processing it.

Use `armada_process_merge_entry` for one reviewed entry. Use
`armada_process_merge_queue` only when the operator intends to start queue
processing. It returns an accepted job and can no-op when a queue run is
already active. Poll the job and merge entry.

Landing behavior comes from the effective landing mode:

| Mode | Result |
| --- | --- |
| `LocalMerge` | Merge into the configured working checkout. Do not push unless separate policy permits it. |
| `PullRequest` | Create or update provider review state. |
| `MergeQueue` | Use the durable integration, test, and landing state machine. |
| `None` | Leave produced work for explicit operator handling. |

Do not infer successful landing from a `Complete` label alone. Verify the
target branch or remote commit that should contain the work.

### 4.8 Close The Record Chain

Before the objective becomes complete:

1. Link the final voyage and missions.
2. Link the passing Checks.
3. Link a release and deployment when work shipped.
4. Link incidents and their final evidence.
5. Create a new record for every deferred task.
6. Update the objective summary with the verified outcome.

## 5. Recovery And Incident Workflow

When autonomous recovery is enabled, Armada classifies failed missions,
creates or updates an incident, records a recovery runbook execution, and can
dispatch a bounded rescue mission. It does not use generic rescue missions for
landing failures. Authentication, quota, review, protected-path, dependency,
and exhausted-recovery failures remain for operator action.

Use this order for manual diagnosis:

1. Read the complete mission and captain logs.
2. Use `armada_captain_diagnostics`.
3. Compare with a known-good mission on the same runtime.
4. Separate provider, host, repository, test, and model failures.
5. Create or update the incident before repeated intervention.
6. Cancel only the exact mission or voyage that must stop.
7. Restart or retry only after the cause is understood.

Incident closure is evidence-driven. Produce a newer passing check, successful
rescue, shipped release, verified deployment, or completed rollback. Do not
close an incident only because a captain reported success.

## 6. Release And Deployment Workflow

Use a workflow profile for repeatable commands. Use named environments for
rollout targets.

1. Create a release and link its objective, voyages, missions, artifacts, and
   Checks.
2. Move the release through its supported states only when evidence permits.
3. Create a deployment against a named environment.
4. Approve it when the environment requires approval.
5. Run deployment verification.
6. Use rollback on the same deployment record if verification fails.
7. Link the related incident and runbook execution.

See [DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for the detailed procedure.

## 7. Configuration And Administration

Treat fleet, vessel, captain, persona, pipeline, playbook, runbook, workflow
profile, environment, template, backup, and server-stop tools as administrator
surfaces. Read the existing record first. Use
`armada_audit_operational_assets` before and after asset changes. Validate
provider models with a live provider call before putting them in a tier.

### Per-captain provider credentials

A captain whose model is served by Zyloo (a `zyloo/` model id) normally uses
the host-level `ZYLOO_KEY` environment variable. A captain may instead carry
its own `apiKey` (and optional `apiBaseUrl`) on its record, which wins over
the environment variable. This lets captains on separate Zyloo subscriptions
run side by side on one Admiral; burn down each subscription, then delete its
captains. The MCP captain surface returns the key masked (last four
characters preserved); the dashboard keeps the raw value so the edit form can
prefill it, and entering the key in the dashboard never passes it through an
orchestrator. Creating a captain whose model is not entitled on the
environment key persists with a warning instead of failing, so a credential
that arrives after creation can be attached from the dashboard.

Vessel instruction files and generated briefing files are protected paths.
Captains must propose instruction changes. The orchestrator reviews and applies
them outside the mission dock.

Backups can contain operational state. Store them in an approved location and
apply retention limits. Restore, delete, purge, stop, and bulk operations need
an explicit operator decision.

## 8. Complete MCP Tool Catalog

The built-in catalog contains 175 names. Some names are compatibility aliases.
Some tool families register only when their service is enabled.

Risk labels:

- **Read**: no intended state change.
- **Write**: creates or updates Armada state.
- **Execute**: starts work, a command, landing, or rollout.
- **Interrupt**: stops or cancels active work.
- **Destructive**: deletes, purges, restores, or stops the server.

### 8.1 Status, Enumeration, Jobs, And Diagnostics

| Risk | Tools |
| --- | --- |
| Read | `armada_status`, `armada_enumerate`, `armada_job_status`, `armada_captain_diagnostics`, `armada_unlanded_branches` |

### 8.2 Fleets And Vessels

| Risk | Tools |
| --- | --- |
| Read | `armada_get_fleet`, `armada_get_vessel` |
| Write | `armada_create_fleet`, `armada_update_fleet`, `armada_add_vessel`, `armada_update_vessel`, `armada_update_vessel_context` |
| Destructive | `armada_delete_fleet`, `armada_delete_fleets`, `armada_delete_vessel`, `armada_delete_vessels` |

### 8.3 Captains

| Risk | Tools |
| --- | --- |
| Read | `armada_get_captain`, `armada_get_captain_log` |
| Write | `armada_create_captain`, `armada_update_captain`, `armada_bench_captain`, `armada_unbench_captain` |
| Interrupt | `armada_stop_captain`, `armada_stop_all` |
| Destructive | `armada_delete_captain`, `armada_delete_captains` |

### 8.4 Voyages And Missions

| Risk | Tools |
| --- | --- |
| Read | `armada_voyage_status`, `armada_mission_status`, `armada_get_mission_diff`, `armada_get_mission_log` |
| Write | `armada_create_mission`, `armada_update_mission`, `armada_transition_mission_status` |
| Execute | `armada_dispatch`, `armada_restart_mission`, `armada_retry_landing` |
| Interrupt | `armada_cancel_mission`, `armada_cancel_voyage` |
| Destructive | `armada_purge_mission`, `armada_delete_missions`, `armada_purge_voyage`, `armada_delete_voyages` |

### 8.5 Planning, Objectives, And Backlog

| Risk | Tools |
| --- | --- |
| Read | `list_objectives`, `list_backlog`, `get_objective`, `get_backlog_item`, `list_backlog_refinement_sessions`, `get_backlog_refinement_session`, `get_backlog_planning_session` |
| Write | `create_objective`, `create_backlog_item`, `update_objective`, `update_backlog_item`, `reorder_objectives`, `reorder_backlog_items`, `create_backlog_refinement_session`, `send_backlog_refinement_message`, `summarize_backlog_refinement_session`, `apply_backlog_refinement_summary`, `stop_backlog_refinement_session`, `create_backlog_planning_session`, `delete_objective`, `delete_backlog_item` |
| Execute | `dispatch_backlog_planning_session`, `armada_decompose_plan`, `armada_parse_architect_output` |

### 8.6 Objective Scheduler

| Risk | Tools |
| --- | --- |
| Read | `armada_objective_scheduler_status` |
| Write | `armada_objective_scheduler_set`, `armada_mark_objective_auto_dispatchable` |

### 8.7 Checks

| Risk | Tools |
| --- | --- |
| Read | `get_check_run` |
| Write | `resolve_check`, `armada_resolve_check` |
| Execute | `run_check`, `retry_check_run` |

`armada_resolve_check` is a compatibility alias for `resolve_check`.

### 8.8 Merge Queue And Audit

| Risk | Tools |
| --- | --- |
| Read | `armada_get_merge_entry`, `armada_drain_audit_queue` |
| Write | `armada_enqueue_merge`, `armada_record_audit_verdict` |
| Execute | `armada_process_merge_entry`, `armada_process_merge_queue` |
| Interrupt | `armada_cancel_merge` |
| Destructive | `armada_delete_merge`, `armada_purge_merge_queue`, `armada_purge_merge_entry`, `armada_purge_merge_entries` |

### 8.9 Docks, Signals, And Events

| Risk | Tools |
| --- | --- |
| Read | `armada_get_dock` |
| Write | `armada_send_signal`, `armada_nudge_voyage`, `armada_mark_signal_read` |
| Destructive | `armada_delete_dock`, `armada_purge_dock`, `armada_delete_docks`, `armada_delete_signals`, `armada_delete_event`, `armada_delete_events` |

### 8.10 Incidents

| Risk | Tools |
| --- | --- |
| Read | `armada_list_incidents`, `armada_get_incident` |
| Write | `armada_create_incident`, `armada_update_incident`, `armada_close_incident` |
| Destructive | `armada_delete_incident` |

### 8.11 Releases

| Risk | Tools |
| --- | --- |
| Read | `get_release` |
| Write | `create_release`, `update_release`, `armada_update_release` |

`armada_update_release` is a compatibility alias for `update_release`.

### 8.12 Deployments

| Risk | Tools |
| --- | --- |
| Read | `get_deployment` |
| Write | `create_deployment`, `update_deployment`, `armada_update_deployment` |
| Execute | `approve_deployment`, `verify_deployment`, `rollback_deployment` |

`armada_update_deployment` is a compatibility alias for
`update_deployment`.

### 8.13 Runbooks

| Risk | Tools |
| --- | --- |
| Read | `list_runbooks`, `get_runbook`, `list_runbook_executions`, `get_runbook_execution` |
| Write | `create_runbook`, `update_runbook`, `update_runbook_execution` |
| Execute | `start_runbook_execution` |
| Destructive | `delete_runbook`, `delete_runbook_execution` |

### 8.14 Workflow Profiles And Environments

| Risk | Tools |
| --- | --- |
| Read | `list_workflow_profiles`, `get_workflow_profile`, `validate_workflow_profile`, `preview_workflow_profile`, `list_environments`, `get_environment`, `armada_audit_operational_assets` |
| Write | `create_workflow_profile`, `update_workflow_profile`, `create_environment`, `update_environment` |
| Destructive | `delete_workflow_profile`, `delete_environment` |

### 8.15 Personas, Pipelines, Playbooks, And Templates

| Risk | Tools |
| --- | --- |
| Read | `get_persona`, `get_pipeline`, `get_playbook`, `list_prompt_templates`, `get_prompt_template` |
| Write | `create_persona`, `update_persona`, `create_pipeline`, `update_pipeline`, `create_playbook`, `update_playbook`, `create_prompt_template`, `update_prompt_template`, `reset_prompt_template` |
| Destructive | `delete_persona`, `delete_pipeline`, `delete_playbook` |

### 8.16 Code Index, Context Packs, And Graphs

| Risk | Tools |
| --- | --- |
| Read | `armada_index_status`, `armada_code_search`, `armada_context_pack`, `armada_fleet_code_search`, `armada_fleet_context_pack`, `armada_graph_search_symbols`, `armada_graph_get_callers`, `armada_graph_get_callees`, `armada_graph_get_impact`, `armada_graph_suggest_affected_tests`, `armada_graph_get_node`, `armada_graph_get_files`, `armada_graph_explore` |
| Execute | `armada_index_update` |

### 8.17 Reflection Memory

| Risk | Tools |
| --- | --- |
| Read | `armada_check_stale_memory` |
| Execute | `armada_consolidate_memory` |
| Write | `armada_accept_memory_proposal`, `armada_reject_memory_proposal` |

### 8.18 AgentWake

| Risk | Tools |
| --- | --- |
| Read | `armada_agentwake_status` |
| Write | `armada_register_agentwake_session` |

AgentWake is a resume transport. It is not the work queue or the source of
truth.

### 8.19 Backup, Restore, And Server Control

| Risk | Tools |
| --- | --- |
| Write | `armada_backup` |
| Destructive | `armada_restore`, `armada_stop_server` |

## 9. Safety Rules

- Read before write.
- Confirm the exact ID before a cancel, delete, purge, restore, rollback, or
  server stop.
- Do not use bulk deletion when a specific record is sufficient.
- Do not retry a dispatch until you know whether the first request created a
  voyage.
- Do not call `resolve_check` to turn an unknown or failed result green.
- Do not accept a reflection proposal without reading the proposed content.
- Do not change vessel context or shared instructions from a captain mission.
- Do not push, deploy, release, or roll back without the applicable operator
  authority.
- Never put credentials or private operational identifiers in public docs,
  prompts, logs, examples, or commit messages.

## 10. Verification Checklist

Before you report completion:

- The objective state matches the evidence.
- The voyage and each mission have the expected terminal state.
- Required Checks passed with real output.
- The target branch contains the expected commit.
- Release and deployment records match what shipped.
- Incidents have evidence for their final state.
- Deferred work has its own record.
- No sensitive value or private operational identifier entered a public
  artifact.
