---
topic: "Personas"
summary: "Built-in persona roles, the Linter and Recorder stages, and prompt-template rules."
read_when: "Choosing or authoring a persona, or wiring one into a pipeline stage."
applies_to: orchestrator
tier: leaf
---
# Armada Personas: Technical Reference

This document describes the current persona implementation. For the operator
procedure, use [OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md).

## Data Model

A persona has a stable ID, tenant, unique name, description, prompt-template
name, built-in flag, active flag, and default playbooks. A prompt template has
its own versioned content. A pipeline stage refers to a persona by name.

Armada seeds twelve built-in personas on startup: the eleven in this table plus
`PriorArtAnalyst` (documented under *Prior-art analyst stage* below). Three
earlier specialist reviewers, `MigrationDataReviewer`, `PerformanceMemoryReviewer`
and `FrontendWorkflowReviewer`, were retired on 2026-09-17 (deactivated, not
deleted) together with their `*Tested` pipelines; no voyage used them:

| Persona | Purpose |
| --- | --- |
| `Worker` | Make the requested code or content change. |
| `Architect` | Decompose broad work into missions. |
| `Product Manager` | Define the user outcome and durable requirements. |
| `Usability Engineer` | Review usability and product consistency. |
| `Judge` | Review correctness and completeness. |
| `TestEngineer` | Add or update tests and verify behavior. |
| `Linter` | Check the changed code and documentation for style and correctness, fix clear in-scope violations, and report findings. Runs in `Tested`, `ProductDevelopment`, and `ReferencePortingTested`. |
| `DiagnosticProtocolReviewer` | Review binary protocols and hardware-risk paths. |
| `TenantSecurityReviewer` | Review authentication, authorization, isolation, and secrets. |
| `PortingReferenceAnalyst` | Compare approved references and parity evidence. |
| `Recorder` | Review the finished work of a voyage and record what is worth remembering into native captain memory. Writes memory only; never changes the repository or shared memory. |
| `PriorArtAnalyst` | Read-only Research analyst that settles whether an objective’s deliverable already exists before a Worker builds it; commits nothing. Conditional stage, not in a default pipeline (see below). |

The seed service reconciles built-in definitions. Built-in personas cannot be
deleted. Custom personas can be created, updated, or deleted.

### Linter finding routing (`lint_finding`)

The Linter reports style and correctness findings in its `## Code Style`,
`## Code Correctness`, `## Documentation`, and `## Residual Issues` sections. The
`lint_finding` typed decision sits on the Linter handoff and routes those
findings for the next stage: only `correctness` or `safety` findings the model
scores at `must_fix` or above are marked **blocking** for the Judge, and a
`style_preference` finding becomes an **evidence note**. The Linter's own output
is unchanged — the decision prepends a routing note to the next brief so taste is
not presented to the Judge as a defect. The decision ships `Gate`; setting it
`Off` lets the Linter output flow unchanged, a settings change, not a persona change.
The deterministic Slop Check (`SlopDiffClassifier`) is unaffected.

### Linter duplication check

The Linter template carries a `## Duplication Check` section. For each method the
mission adds or substantially rewrites, the Linter calls
`armada_mission_code_search` with its mission id and the new code body, reads
every strong result, and reports a confirmed duplicate under `## Residual Issues`
as `[consistency | should_fix] DRY: <new path:line> duplicates <existing
path:line> - <shared behavior>`. It never fixes a duplicate, because moving
shared logic changes files outside the diff. When the tool is absent or
unavailable, or warns that the index is stale or lexical only, the Linter says so
instead of reporting no duplication. The section is added to the embedded default
and, by an append-if-missing upgrader, to an existing built-in row, so an
operator edit is kept. The finding headings are unchanged.

### Prior-art analyst stage (`prior_art`)

The `prior_art` typed decision does not add a persona to any pipeline by default: it is a
retrieval step plus typed questions inside stages that already run (the dispatch
preflight and the Worker-to-Judge handoff), so no persona runs for nothing. It
names one conditional persona, **PriorArtAnalyst**, a read-only Research analyst.
The persona is seeded as a built-in (template `persona.prior_art_analyst`) and
joins no built-in pipeline. It runs detached and an empty diff is its normal
result.
When the model's `already_done` reading lands in the uncertain band (`0.4`–`0.7`)
on a large objective — the one case the preflight and Judge seams cannot settle —
the dispatch preview adds a `prior_art_analyst_stage_recommended` advisory
suggesting the operator insert a PriorArtAnalyst Research stage before the Worker,
briefed with the retrieved candidates, to answer the single question ("does the
objective's deliverable already exist?") and write its finding into the brief. The
stage is read-only (it commits nothing) and runs only in that band, which is the
DRY answer to "more personas": the analyst runs when the question is genuinely
open and never otherwise. The recommendation is advisory — the operator confirms
it; the adapter never inserts a stage by itself. The decision ships `Gate`.

## Prompt Assembly

When Armada builds a captain prompt, it resolves the persona, loads its active
prompt template, and adds mission context and selected playbooks. Default
playbooks merge from fleet, vessel, persona, captain, and mission layers.

When commit metadata is on and the mission is not read-only, the launch prompt
ends with the `commit.instructions_preamble` text and the Armada trailers. The
preamble requires a summary line and a full manifest of every file added,
modified or deleted, with what changed and why.

Persona prompts describe behavior. They must not contain fixed provider model
names. Use a pipeline stage model tier of `low`, `mid`, or `high` when a stage
needs a preference.

Supported captains receive the local Armada MCP connection. A persona must use
it only for mission-scoped coordination and evidence. The operator owns fleet
control, dispatch, deployment, restore, purge, and server actions unless the
mission explicitly assigns such an action.

## Operator API

Use these MCP tools:

- `armada_enumerate` with `entityType: "personas"`;
- `get_persona`;
- `create_persona`;
- `update_persona`;
- `delete_persona`;
- `list_prompt_templates`;
- `get_prompt_template`;
- `create_prompt_template`;
- `update_prompt_template`;
- `reset_prompt_template`.

`create_persona` and `update_persona` accept `defaultPlaybooks`. Read the
current persona before you replace that list. Do not attach inactive
playbooks.

## Captain Interaction

A captain can have a default persona and default playbooks. Mission dispatch
can select a different persona. Pipeline dispatch selects the persona for each
stage. Captain defaults do not override an explicit mission persona.

Use `armada_create_captain` and `armada_update_captain` to manage captain
defaults. Use `armada_captain_status` to inspect runtime state. Do not put MCP
credentials, server-control instructions, or destructive operator procedures
in captain prompt templates.

### The `[ARMADA:BLOCKING]` marker is prose only

Some Judge and reviewer prompt templates emit an `[ARMADA:BLOCKING]` line to
call out a change a captain must make before the work can pass. No parser reads
it: unlike `[ARMADA:RESULT]` and `[ARMADA:VERDICT]`, it is prose the operator
reads on the Judge's brief, not a structured signal. Do not rely on it to gate
anything automatically; the Judge's `[ARMADA:VERDICT]` line is the structured
outcome.

## Validation

Before release, confirm:

1. All 12 built-in personas exist and are active. The three orphan reviewers (MigrationDataReviewer, PerformanceMemoryReviewer, FrontendWorkflowReviewer) were retired 2026-09-17 and are inactive.
2. Each active persona refers to an active prompt template.
3. Every pipeline stage refers to an active persona.
4. Default playbook references exist and are active.
5. Captain prompts do not advertise operator-control MCP tools unless the
   mission explicitly assigns that action.
6. The built-in persona and pipeline seed tests pass.
