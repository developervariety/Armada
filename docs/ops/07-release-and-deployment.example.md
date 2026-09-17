---
topic: "Release And Deployment Workflow"
summary: "The release-to-deployment record flow and how this deployment upgrades its Admiral."
read_when: "Shipping a release, verifying or rolling back a deployment, or upgrading the running Admiral."
applies_to: orchestrator
tier: leaf
---
# Release And Deployment Workflow

> Template. Copy to `07-release-and-deployment.md` in this folder and fill it in for your
> deployment. The filled copy is operator-local and is not committed.

Cover at least:

- Pre-deploy: pause the scheduler, engage the dispatch hold, back up the database.
- Build and rehearse the candidate against a restored copy.
- Swap, verify health and schema version, then restore only the authorized scheduler state.
- Rollback: the retained image tags and the exact command shape (placeholders only).
