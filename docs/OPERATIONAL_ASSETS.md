# Armada Operational Assets

This guide is the canonical procedure for playbooks, runbooks, workflow
profiles, environments, prompt templates, personas, pipelines, and their
default links. These records are durable operator configuration. They are not
captain-owned files.

Use [armada-ops.md](armada-ops.md) for the complete operating loop and MCP
catalog. Use [DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and
deployment records.

## 1. Operator Boundary

Armada MCP is an operator surface. Do not expose its catalog to captains.
Captains do not need dispatch, fleet administration, deployment, restore,
purge, or server-control tools. Give a captain only the runtime tools and
files required by its mission.

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
| `persona-*-learned` or `vessel-*-learned` | Learned memory; use only when the feature and owner policy enable it |

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
partial replacement.

At minimum, a code-producing vessel needs a real Build command and a real
UnitTest command. Use a containerless unit-test command only when the normal
suite has fixtures that require an unavailable container runtime. Do not use a
filter to hide ordinary failures.

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
- `DiagnosticProtocolReviewer`
- `TenantSecurityReviewer`
- `MigrationDataReviewer`
- `PerformanceMemoryReviewer`
- `PortingReferenceAnalyst`
- `FrontendWorkflowReviewer`
- `MemoryConsolidator`

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
| `Tested` | Worker, TestEngineer, Judge |
| `FullPipeline` | Architect, Worker, TestEngineer, Judge |
| `ProductDevelopment` | Product Manager, Architect, Worker, Usability Engineer, TestEngineer, Judge |
| `DiagnosticProtocolTested` | Worker, DiagnosticProtocolReviewer, TestEngineer, Judge |
| `TenantSecurityTested` | Worker, TenantSecurityReviewer, TestEngineer, Judge |
| `MigrationDataTested` | Worker, MigrationDataReviewer, TestEngineer, Judge |
| `PerformanceMemoryTested` | Worker, PerformanceMemoryReviewer, TestEngineer, Judge |
| `ReferencePortingTested` | Worker, PortingReferenceAnalyst, TestEngineer, Judge |
| `FrontendWorkflowTested` | Worker, FrontendWorkflowReviewer, TestEngineer, Judge |
| `Reflections` | MemoryConsolidator |
| `ReflectionsDualJudge` | MemoryConsolidator, then two parallel Judge stages |

Use `armada_enumerate(entityType: "pipelines")`, `get_pipeline`,
`create_pipeline`, `update_pipeline`, and `delete_pipeline`.

Pipeline selection order is explicit dispatch, vessel default, fleet default,
then `WorkerOnly`. A missing default therefore changes behavior. Check the
resolved pipeline before dispatch. Do not shorten an approved full pipeline
only to reduce time or cost.

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
- The Armada MCP catalog remains operator-only.
