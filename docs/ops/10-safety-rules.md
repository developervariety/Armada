---
topic: "Safety Rules"
summary: "The non-negotiable operator rules: read before write, confirm ids, never hide a failure, never leak a secret."
read_when: "Before any cancel, delete, purge, restore, rollback, or server stop."
applies_to: orchestrator
tier: leaf
---
# Safety Rules

- Read before write.
- Confirm the exact ID before a cancel, delete, purge, restore, rollback, or
  server stop.
- Do not use bulk deletion when a specific record is sufficient.
- Do not retry a dispatch until you know whether the first request created a
  voyage.
- Do not call `resolve_check` to turn an unknown or failed result green.
- Do not change vessel context or shared instructions from a captain mission.
- Do not push, deploy, release, or roll back without the applicable operator
  authority.
- Never put credentials or private operational identifiers in public docs,
  prompts, logs, examples, or commit messages.
