---
topic: "Safety Rules"
summary: "The non-negotiable operator rules for this deployment."
read_when: "Before any cancel, delete, purge, restore, rollback, or server stop."
applies_to: orchestrator
tier: leaf
---
# Safety Rules

> Template. Copy to `10-safety-rules.md` in this folder and fill it in for your
> deployment. The filled copy is operator-local and is not committed.

Cover at least:

- Read a record before changing it; confirm exact ids.
- Never hide a failure (no resolve to mask a red Check).
- Never place credentials, hosts, paths, or private ids in a tracked file.
