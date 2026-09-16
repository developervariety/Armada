---
topic: "Selective Upstream Integration"
summary: "How the fork absorbs upstream changes: an incremental cherry-pick sync, not a blanket merge."
read_when: "Working on upstream absorption, or checking whether a reviewed upstream feature is live."
applies_to: orchestrator
tier: leaf
---
# Selective Upstream Integration

See the **Upstream sync protocol** in the repository `CLAUDE.md` for the
incremental cherry-pick procedure and the current sync baseline. The fork keeps
parity by re-implementing selected upstream features rather than merging, so a
blanket merge is never the path. Keep current objectives and delivery state in
Armada. Armada platform work uses direct edits; the
review campaign must not dispatch voyages, missions or planning captains.
