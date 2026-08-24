# Armada Personas: Technical Reference

This document describes the current persona implementation. For the operator
procedure, use [OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md).

## Data Model

A persona has a stable ID, tenant, unique name, description, prompt-template
name, built-in flag, active flag, and default playbooks. A prompt template has
its own versioned content. A pipeline stage refers to a persona by name.

Armada seeds 13 built-in personas on startup:

| Persona | Purpose |
| --- | --- |
| `Worker` | Make the requested code or content change. |
| `Architect` | Decompose broad work into missions. |
| `Product Manager` | Define the user outcome and durable requirements. |
| `Usability Engineer` | Review usability and product consistency. |
| `Judge` | Review correctness and completeness. |
| `TestEngineer` | Add or update tests and verify behavior. |
| `DiagnosticProtocolReviewer` | Review binary protocols and hardware-risk paths. |
| `TenantSecurityReviewer` | Review authentication, authorization, isolation, and secrets. |
| `MigrationDataReviewer` | Review migrations, provider parity, rollback, and data safety. |
| `PerformanceMemoryReviewer` | Review allocations, lifetime, throughput, and retained data. |
| `PortingReferenceAnalyst` | Compare approved references and parity evidence. |
| `FrontendWorkflowReviewer` | Review frontend workflow, accessibility, i18n, and responsive states. |
| `MemoryConsolidator` | Produce a learned-memory proposal from completed evidence. |

The seed service reconciles built-in definitions. Built-in personas cannot be
deleted. Custom personas can be created, updated, or deleted.

## Prompt Assembly

When Armada builds a captain prompt, it resolves the persona, loads its active
prompt template, and adds mission context and selected playbooks. Default
playbooks merge from fleet, vessel, persona, captain, and mission layers.

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
current persona before you replace that list. Do not attach inactive or
learned playbooks unless the feature and owner policy enable them.

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
