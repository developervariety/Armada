# Armada Operator Guide

This guide is the canonical operating procedure for Armada. It describes how an
orchestrator creates, monitors, verifies, lands, and closes work, and it lists
every MCP tool the server registers.

The guide is split into per-chapter files under [`ops/`](ops/) so an
orchestrator can load only the chapter it needs instead of one large file. Each
chapter file opens with front-matter (`topic`, `summary`, `read_when`,
`applies_to`, `tier`) for retrieval. Section numbers such as `4.6` and `8.19`
still appear inside the chapters, and a cross-reference to one resolves to its
chapter file below.

**The chapters are operator-local.** A filled chapter names a deployment's real
hosts, vessels, accounts, and policy, so git ignores it. The repository tracks a
template for each chapter (`ops/NN-<chapter>.example.md`); copy it without
`.example` and fill it in. See [`ops/README.md`](ops/README.md). Product
reference that every deployment shares stays tracked: the
[MCP tool catalog](MCP_TOOL_CATALOG.md), [typed decisions](TYPED_DECISIONS.md),
and [model routing](USAGE_ROUTING.md).

Use [ORCHESTRATOR_INSTRUCTIONS.md](ORCHESTRATOR_INSTRUCTIONS.md) as the prompt bootstrap for any orchestrator runtime. Use [MCP_API.md](MCP_API.md) for transport and schema discovery. Use
[DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and deployment
detail. Use [MERGING.md](MERGING.md), [PIPELINES.md](PIPELINES.md), and
[SCHEDULING.md](SCHEDULING.md) for subsystem detail. Use
[OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md) for playbooks, runbooks, workflow
profiles, environments, personas, pipelines, and their links.

## Chapters

| # | Chapter | Read when |
| --- | --- | --- |
| 1 | [Source Of Truth](ops/01-source-of-truth.md) | Deciding what to trust as the state of work, delivery, or a result. |
| 2 | [Selective Upstream Integration](ops/02-selective-upstream-integration.md) | Working on upstream absorption, or checking whether a reviewed upstream feature is live. |
| 3 | [Available Features And Active Policy](ops/03-available-features-and-policy.md) | Before depending on a feature such as code indexing, autonomous recovery, or a landing mode. |
| 4 | [Connection And Discovery](ops/04-connection-and-discovery.md) | Connecting a client to the Admiral, or starting an operator session. |
| 5 | [Standard Workflow](ops/05-standard-workflow.md) | Dispatching and running any non-trivial work through to a closed record. |
| 6 | [Recovery And Incident Workflow](ops/06-recovery-and-incidents.md) | A mission or voyage failed, or an incident needs driving to closure. |
| 7 | [Release And Deployment Workflow](ops/07-release-and-deployment.md) | Shipping a release, verifying or rolling back a deployment, or upgrading the running Admiral. |
| 8 | [Configuration And Administration](ops/08-configuration-and-administration.md) | Changing settings, routing, migrations, or the typed-decision configuration. |
| 9 | [Complete MCP Tool Catalog](MCP_TOOL_CATALOG.md) (tracked) | Choosing a tool, or checking what a tool family does and its risk. |
| 10 | [Safety Rules](ops/10-safety-rules.md) | Before any cancel, delete, purge, restore, rollback, or server stop. |
| 11 | [Verification Checklist](ops/11-verification-checklist.md) | Before reporting completion of any objective or task. |
