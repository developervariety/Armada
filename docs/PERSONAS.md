# Armada Personas: Technical Reference

This document describes the current persona implementation. For the operator
procedure, use [OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md).

## Data Model

A persona has a stable ID, tenant, unique name, description, prompt-template
name, built-in flag, active flag, and default playbooks. A prompt template has
its own versioned content. A pipeline stage refers to a persona by name.

Armada seeds eight built-in personas on startup. The three specialist
reviewers in this table are seeded only from settings:

| Persona | Purpose |
| --- | --- |
| `Worker` | Make the requested code or content change. |
| `Architect` | Decompose broad work into missions. |
| `Product Manager` | Define the user outcome and durable requirements. |
| `Usability Engineer` | Review usability and product consistency. |
| `Judge` | Review correctness and completeness. |
| `TestEngineer` | Add or update tests and verify behavior. |
| `Linter` | Check the changed code and documentation for style and correctness, fix clear in-scope violations, and report findings. Runs in ProductDevelopment only. |
| `DiagnosticProtocolReviewer` | Review binary protocols and hardware-risk paths. |
| `TenantSecurityReviewer` | Review authentication, authorization, isolation, and secrets. |
| `PortingReferenceAnalyst` | Compare approved references and parity evidence. |
| `Recorder` | Review the finished work of a voyage and record what is worth remembering into native captain memory. Writes memory only; never changes the repository or shared memory. |

The seed service reconciles built-in definitions. Built-in personas cannot be
deleted. Custom personas can be created, updated, or deleted.

### Linter finding routing (D24 `lint_finding`)

The Linter reports style and correctness findings in its `## Code Style`,
`## Code Correctness`, `## Documentation`, and `## Residual Issues` sections. The
D24 `lint_finding` typed decision sits on the Linter handoff and routes those
findings for the next stage: only `correctness` or `safety` findings the model
scores at `must_fix` or above are marked **blocking** for the Judge, and a
`style_preference` finding becomes an **evidence note**. The Linter's own output
is unchanged — the decision prepends a routing note to the next brief so taste is
not presented to the Judge as a defect. The decision ships `Off` (the Linter
output flows unchanged); a Gate flip is a settings change, not a persona change.
The deterministic Slop Check (`SlopDiffClassifier`) is unaffected.

### Prior-art analyst stage (D26 `prior_art`)

D26 `prior_art` does not add a persona to any pipeline by default: it is a
retrieval step plus typed questions inside stages that already run (the dispatch
preflight and the Worker-to-Judge handoff), so no persona runs for nothing. It
names one conditional persona, **PriorArtAnalyst**, a read-only Research analyst.
When the model's `already_done` reading lands in the uncertain band (`0.4`–`0.7`)
on a large objective — the one case the preflight and Judge seams cannot settle —
the dispatch preview adds a `prior_art_analyst_stage_recommended` advisory
suggesting the operator insert a PriorArtAnalyst Research stage before the Worker,
briefed with the retrieved candidates, to answer the single question ("does the
objective's deliverable already exist?") and write its finding into the brief. The
stage is read-only (it commits nothing) and runs only in that band, which is the
DRY answer to "more personas": the analyst runs when the question is genuinely
open and never otherwise. The recommendation is advisory — the operator confirms
it; the adapter never inserts a stage by itself. The decision ships `Off`.

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

## Validation

Before release, confirm:

1. All 13 built-in personas exist and are active.
2. Each active persona refers to an active prompt template.
3. Every pipeline stage refers to an active persona.
4. Default playbook references exist and are active.
5. Captain prompts do not advertise operator-control MCP tools unless the
   mission explicitly assigns that action.
6. The built-in persona and pipeline seed tests pass.
