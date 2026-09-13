# Armada Capabilities

This document describes the high-level capabilities of the Armada multi-agent orchestration system.

---

## Autonomous Operation And Wake Delivery

**Status:** Shipped

Armada's built-in objective scheduler dispatches eligible objectives up to its
configured concurrency limit and uses the same dispatch hold and Build/UnitTest
Check arming path as operator dispatch. Scheduler state changed through MCP is
persisted and survives an Admiral restart.

The coordination board provides claims, presence, addressed notes, and full
`UnreadWakes` payloads. Addressed notes always retain a Wake signal. A matching
AgentWake's persistent participant key can also start Claude, Codex, or OpenCode
when delivery is `SpawnProcess` or `Both`; a transient registration can override
that key for a controlled session. OpenCode starts a fresh session and reconstructs
state from the note, board, and durable memory.

Bounded read-only helper processes use `scripts/autonomy/spawn-helper.sh`.
Its `offer` mode gives an operator a bounded reassignment window before
fallback work starts. One process owns one participant key; do not combine a
resident helper with an AgentWake process owner for the same key.

The standalone lead runner and Grok Bot integration are retired. See the
[retirement archive](archive/autonomous-lead/README.md).
