---
topic: "Operational Assets"
summary: "Playbooks, runbooks, workflow profiles, environments, personas, pipelines, and their default links."
read_when: "Creating or changing an operational asset, or auditing what a vessel resolves."
applies_to: orchestrator
tier: leaf
---
# Armada Operational Assets

This guide is the canonical procedure for playbooks, runbooks, workflow
profiles, environments, prompt templates, personas, pipelines, and their
default links. These records are durable operator configuration. They are not
captain-owned files.

Use [armada-ops.md](armada-ops.md) for the complete operating loop and MCP
catalog. Use [DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and
deployment records.

## 1. Operator Boundary

Armada gives supported captains an explicit launch-scoped connection to the
local MCP URL. This makes coordination and evidence tools available even when
the runtime ignores project configuration. Mission scope remains authoritative:
captains must not dispatch, administer the fleet, deploy, restore, purge, or
control the server unless the mission explicitly assigns that operator action.

Launch-scoped client settings select the MCP connection and configuration files.
They are not filesystem permissions. Access to an absolute host path still depends
on the process user, container mounts, and host permissions. Do not treat a scoped
HOME or an isolated worktree as proof that operator files cannot be read.
Captains must not read operator settings, credentials, or broad operator logs to
discover validation commands or input values.

An operator uses MCP to:

- inspect and manage operational assets;
- select a vessel pipeline and playbooks;
- create Checks and delivery records;
- monitor and control missions;
- record runbook evidence.

Run `armada_audit_operational_assets` before and after a group of asset
changes. Treat errors as blockers. Review warnings before dispatch.

## 2. Asset Ownership

| Asset | Owns | Does not own |
| --- | --- | --- |
| Playbook | Reusable task or repository guidance | Mission scope or live state |
| Runbook | Repeatable operator procedure and execution evidence | General coding guidance |
| Workflow profile | Commands and expected artifacts | Deployment target metadata |
| Environment | Named target, approval, health, and rollout rules | Build and test commands |
| Prompt template | One prompt module | Persona routing |
| Persona | A named mission role and its prompt template | Provider model selection |
| Pipeline | Ordered or parallel persona stages | Per-mission scope |
| Fleet, vessel, persona, captain defaults | Automatic playbook and pipeline selection | Proof that a mission used the asset correctly |

Do not put live IDs in public documentation. Resolve IDs from current Armada
records at the start of the operation.

## 3. Playbooks

### 3.1 Classes

Use file-name prefixes to make intent clear:

| Prefix | Purpose |
| --- | --- |
| `proj-` | Shared project guardrails or mission contracts |
| `vessel-` | Repository-specific code or test rules |
| `dn-` | Opt-in .NET subject guidance |
| `fe-` | Opt-in frontend guidance |
| `RUNBOOK-` or `system/` | Playbook-backed runbook |

Imported subject playbooks remain opt-in. Do not attach a large library to
every mission. Select the smallest set that contains required guidance.

### 3.2 Delivery modes

| Mode | Use |
| --- | --- |
| `InstructionWithReference` | Tell the runtime which staged or available reference to read |
| `InlineFullContent` | Put small, essential content in the prompt |
| `AttachIntoWorktree` | Place a file in the dock when the runtime must read it as a file |

Prefer references or dock files for long content. Measure prompt size after
selection. Do not attach an inactive playbook.

### 3.3 Default merge order

Armada merges defaults in this order:

1. fleet;
2. vessel;
3. persona;
4. captain;
5. voyage or mission selection.

A later entry wins when the same playbook ID appears more than once. Read all
layers before you change one. A default reference to a missing or inactive
playbook is configuration drift.

### 3.4 MCP operations

- Read: `armada_enumerate(entityType: "playbooks")`, `get_playbook`.
- Write: `create_playbook`, `update_playbook`.
- Delete: `delete_playbook`.
- Defaults: `armada_update_fleet`, `armada_update_vessel`,
  `update_persona`, `armada_create_captain`, and `armada_update_captain`.

Read the current owner record before changing defaults. Pass an empty default
playbook list only when the intent is to clear the layer.

## 4. Runbooks

A runbook is a playbook with Armada runbook metadata. It has parameters,
ordered steps, optional workflow and environment bindings, and a default
Check type.

A useful runbook step states:

- the exact tool or command;
- required input;
- expected evidence;
- the stop or escalation condition;
- the record that receives the result.

Do not write a step such as "verify the deployment" without naming the Check,
health query, deployment record, and failure action.

### 4.1 Lifecycle

1. Find the procedure with `list_runbooks`.
2. Read it with `get_runbook`.
3. Start evidence capture with `start_runbook_execution`.
4. Perform one step at a time.
5. Record completed step IDs and notes with `update_runbook_execution`.
6. Set a terminal status only when the evidence supports it.
7. Read the final record with `get_runbook_execution`.

Use `list_runbook_executions` to find an existing execution before you start a
duplicate. Use `create_runbook` and `update_runbook` for procedures. Use
`delete_runbook` or `delete_runbook_execution` only with explicit destructive
authority.

The server-owned recovery runbook is part of autonomous recovery. Do not use
it as a general operator checklist.

## 5. Workflow Profiles

Generated captain briefs include a bounded native-check reference: configured
validation commands and environment names, with profile environment values
withheld. Credential-shaped commands are withheld rather than made executable;
truncated or redacted commands must not be copied into a shell. This reference
does not add stage duties or change which native Checks are armed. A manual run
with missing profile inputs must name the missing inputs and resulting skips;
it is not full-profile Check proof. Native Checks apply the complete profile
environment. Deployment and rollback commands are not included in this reference.

A workflow profile owns repeatable commands. The resolution order is:

1. explicit profile ID;
2. active vessel profile;
3. active fleet profile;
4. active global profile.

Within a scope, the default profile wins. If no default exists, the most
recent active profile wins.

Use these tools:

- `list_workflow_profiles` and `get_workflow_profile` for inspection;
- `validate_workflow_profile` before every create or update;
- `preview_workflow_profile` to confirm vessel resolution and command output;
- `create_workflow_profile` and `update_workflow_profile` for complete records;
- `delete_workflow_profile` only after checking Checks, releases, deployments,
  runbooks, and environment seeding that can refer to it.

The create and update tools take a complete `profile` object. Do not send a
partial replacement. Update replaces every caller-settable field, including
`environmentVariables`; identity, owner, tenant and creation time are kept.
REST (`POST`, `PUT /api/v1/workflow-profiles`) and MCP share one service: ids
and commands are trimmed, a new default clears the other defaults of its scope,
and a global administrator's profile belongs to the tenant it names, else the
tenant of the fleet or vessel it is scoped to. Validation answers exactly what a
create would do for the same caller, and it reads fleets and vessels within the
caller's scope, so another tenant's fleet or vessel reads as not found. The
project-profile validate route follows the same rule and needs a tenant
administrator.

At minimum, a code-producing vessel needs a real Build command and a real
UnitTest command. Use a containerless unit-test command only when the normal
suite has fixtures that require an unavailable container runtime. Do not use a
filter to hide ordinary failures.

`abbreviatedUnitTestCommand` is an optional fast subset stored beside
`unitTestCommand`. Checks and the definition-of-done gate keep running
`unitTestCommand`. An empty abbreviated command stays empty. It does not by
itself satisfy the rule that a profile has at least one command, because no
check selects it.

`GET /api/v1/vessels/{id}/toolchain-suggestion` and
`armada_suggest_vessel_toolchain` read marker files in the vessel working
directory and return suggested build, test and lint commands. The suggestion
does not create or change a workflow profile or a vessel command. The operator
copies a suggestion into a profile when the operator wants it.

When the definition-of-done gate or a UnitTest or IntegrationTest Check sees a
red test run, it re-runs the failing tests once alone; a test that lost a race
under load passes alone and a real defect fails again. Both apply one rule: only
a test failure with a complete set of failing names, at most three tests (or
three classes for `dotnet test`), is re-run, and the result of the re-run
decides. A Check that passes this way records `passed on isolated re-run` in its
summary, with the full run's exit code and test counts, the re-run tests, and the
re-run outcome; its output carries the re-run command and output after the full
run. A Check whose re-run fails, or that cannot re-run, stays `Failed` and its
summary says which. A landing's integration Check also names, for each failing
test, whether a file named for its class is in the change since the target tip.
The definition-of-done setting `RerunFailingClassesOnce` turns the re-run off for
the gate and the Checks together. Two optional fields let a vessel whose runner
is not `dotnet test` take part:

- `failedTestLinePattern` is a .NET regular expression with a named group
  `name` that reads one failing test name from one output line, for example
  `^FAIL (?<name>.+?): ` for a runner that prints `FAIL <test name>: <message>`.
  The gate tries it on each line after its built-in matchers (dotnet VSTest,
  pytest, unittest). The `testOutcomes` of a check run's bounded view
  (`get_check_run`, `run_check`, `retry_check_run`) reads the same way, with
  the pattern of the profile the run recorded. A save keeps the pattern exactly as written, without
  trimming, and refuses a pattern that does not compile or has no
  `name` group. Each match has a 250 ms timeout; a pattern that exceeds it marks
  the names unreadable, and nothing is re-run.
- `isolatedTestRerunCommand` is a complete command that re-runs only the named
  tests and every other step of the unit-test command. The gate and the Checks
  pass the failing names in the `ARMADA_RERUN_TESTS` environment variable, one
  per line, and never write a name into the command text. They re-run at most
  three names this way.

Without `isolatedTestRerunCommand`, the gate and the Checks narrow a
`dotnet test` command to the failing classes themselves. Any other command is not re-run, and the failure
text names the missing field. A consumer suite uses the consumer's own profile.

A failed gate command's failure text opens with the test outcomes it read from
the whole output, standard output and standard error together, before any
bounding. `--- FAILING TESTS ---` lists every failing test with the first
exception line printed after it (or the message on the failure line itself).
`--- SKIPPED TESTS ---` lists every skipped test; when there are more than 100,
or the run summary counts more skips than the runner named, it states the count
and names `armada_get_mission_log` with the report's `captureId` as the place
for the full list. The bounded diagnostics and output tail follow. The rescue
brief carries the same text.

`gateCommandTimeoutSeconds` is the time limit, in seconds, of each
definition-of-done gate command run with this profile: the vessel's build, its
unit-test command and that command's post-test-step retry, and, on a consumer's
profile, the consumer's build and tests. Empty uses the global
`DefinitionOfDone.CommandTimeoutSeconds`. A save refuses a value outside 30 to
3600. The mission's definition-of-done report (`GET
/api/v1/missions/{id}/definition-of-done`) shows the limit in effect as
`Configuration.CommandTimeoutSeconds` and where it comes from as
`Configuration.CommandTimeoutSource` (`workflow_profile` or `settings`). Set it
for a suite whose normal run under host load is close to the global limit.

`gateInputPaths` lists the repository-relative files or glob patterns that the
profile's required checks read, for example `docs/data-dictionary.md` for a
docs check that compares a data dictionary with the schema. Commands are shell
strings, so Armada never derives this list; the operator declares it. A save
trims each entry, uses forward slashes, drops blanks and duplicates, keeps at
most 50 entries, and refuses an entry that is rooted or holds a `..` segment.
An Architect brief on a vessel whose profile declares the list carries a
`Gate Input Files` section: when the planned change touches what an entry
describes, the plan assigns that file to the mission that makes the change.
When an Architect plan fans out and no planned mission's title or description
names an entry, by its exact path or through its glob, Armada records a
`mission.architect_plan_gate_inputs_missing` event that names the entries. The
plan still fans out. `preview_objective_dispatch` warns with
`gate_inputs_unplanned` before dispatch.

Commands run on the dock host. Use that host's path and shell syntax. A
Windows `.bat`, `%CD%`, or `.\\` command is invalid on a Linux Admiral host.

Environment-specific entries can define deploy, rollback, smoke, health,
deployment-verification, and rollback-verification commands. A build command
is not a deployment command. `git status` is not rollback verification.

## 6. Environments

An environment is a named deployment target for one vessel. Use:

- `list_environments` and `get_environment` to inspect it;
- `create_environment` and `update_environment` to manage it;
- `delete_environment` only after checking linked deployments, incidents, and
  runbooks.

One active default environment per vessel is allowed. Production normally
requires explicit approval.

A seeded environment can be metadata-only. The words Staging or Production do
not prove that a deploy command, base URL, health endpoint, or rollback path
exists. Before a real deployment, confirm all of these:

- the workflow profile has a deploy command for the environment;
- the environment names the real target;
- approval policy is correct;
- verification and monitoring are configured;
- rollback and rollback verification are real operations.

If these items are absent, use the environment only for verification records.
Do not claim that a deployment occurred.

## 7. Personas And Prompt Templates

A persona names a role. Its prompt template defines the role instructions.
Every active persona must reference one active prompt template.

Built-in personas:

- `Worker`
- `Architect`
- `Product Manager`
- `Usability Engineer`
- `Judge`
- `TestEngineer`
- `Linter`
- `Recorder`

Specialist reviewer personas (`DiagnosticProtocolReviewer`,
`TenantSecurityReviewer`, `PortingReferenceAnalyst`) are not built in. Seed them from
`additionalPersonas` and `additionalPromptTemplates` in settings (see
`factory/settings.fleet.example.json`). They are also editable on the
Dashboard Settings page. They load at startup.

Use `armada_enumerate(entityType: "personas")`, `get_persona`,
`create_persona`, `update_persona`, and `delete_persona`. Built-in personas
cannot be deleted. Use `list_prompt_templates`, `get_prompt_template`,
`create_prompt_template`, `update_prompt_template`, and
`reset_prompt_template` for templates.

Do not put concrete provider models in persona prompts. Pipeline stages use
the `low`, `mid`, or `high` model tier when a preference is necessary.

## 8. Pipelines

Built-in pipelines:

| Pipeline | Stages |
| --- | --- |
| `WorkerOnly` | Worker |
| `Reviewed` | Worker, Judge |
| `Tested` | Worker, TestEngineer, Linter, Judge |
| `FullPipeline` | Architect, Worker, TestEngineer, Judge |
| `ProductDevelopment` | Product Manager, Architect, Worker, Usability Engineer, TestEngineer, Linter, Judge, Recorder |
| `Recorded` | Worker, Recorder |

Specialist-tested pipelines (`DiagnosticProtocolTested`,
`TenantSecurityTested`, `ReferencePortingTested`) are not built in.
Seed them from `additionalPipelines` in settings. They load at startup.

Retire a seeded pipeline by deactivating it; the seed keeps it inactive across
restarts. A seeded pipeline cannot be deleted while a seed still declares it. One
that is no longer declared stops being built in at the next start and can then be
deleted. [Pipelines](PIPELINES.md#built-in-pipelines) owns the rule.

Use `armada_enumerate(entityType: "pipelines")`, `get_pipeline`,
`create_pipeline`, `update_pipeline`, and `delete_pipeline`.

Pipeline selection order is explicit dispatch, vessel default, fleet default,
then `WorkerOnly`. A missing default therefore changes behavior. Check the
resolved pipeline before dispatch. Do not shorten an approved full pipeline
only to reduce time or cost. An inactive pipeline is refused by name wherever it
resolves, and no default or objective can be pointed at one;
[Pipelines](PIPELINES.md#resolution) owns the rule. Before you deactivate a
pipeline, move every vessel, fleet and objective that names it to an active one.

Parallel stages share the same stage order. They are siblings, not a duplicate
database error.

## 9. Change Procedure

For a group of operational-asset changes:

1. Call `armada_status` and confirm no active work depends on the records.
2. Call `armada_audit_operational_assets`.
3. Export or read every target record.
4. Validate proposed profile, runbook, template, and default links.
5. Apply the smallest change.
6. Preview profile and pipeline resolution for affected vessels.
7. Run the relevant Check in an isolated checkout.
8. Call `armada_audit_operational_assets` again.
9. Record changed names and evidence in the operator report.

Do not change database tables directly for ordinary administration. Use MCP,
REST, or the dashboard so validation and events remain intact.

## 10. Completion Checklist

- Every active persona has an active prompt template.
- Every pipeline stage has an active persona.
- Every default playbook exists and is active.
- Every managed code vessel resolves an active workflow profile.
- Build and unit-test commands match the execution host.
- A named deployment environment does not imply an unconfigured deployment.
- Runbook executions contain step evidence and a justified terminal status.
- Captains receive the local Armada MCP connection; operator-control tools stay
  outside normal mission scope.

## Account usage and persona routing

See [Smart Routing](USAGE_ROUTING.md) for the opt-in policy (usage filter,
persona model lists, the `capacity_escalation` decision, route restrictions),
collectors, credential references, and Dashboard controls. The settings REST API
exposes `providerUsage`; `POST /api/v1/settings/usage-preview` previews the
Legacy Routing order, usage verdicts, model groups, capacity reading, and chosen
captain for a saved or draft policy with settings write permission. No new MCP tool is
required. Policy updates use `PUT /api/v1/settings` and hot-reload.
