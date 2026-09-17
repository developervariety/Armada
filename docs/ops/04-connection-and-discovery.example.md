---
topic: "Connection And Discovery"
summary: "MCP transport, authentication, tool pagination, and the start-of-session read sequence."
read_when: "Connecting a client to the Admiral, or starting an operator session."
applies_to: orchestrator
tier: leaf
---
# Connection And Discovery

> Template. Copy to `04-connection-and-discovery.md` in this folder and fill it in for your
> deployment. The filled copy is operator-local and is not committed.

Cover at least:

- How clients reach the Admiral (local URL, SSH bridge); use placeholders such as `<server-host>`.
- Where the credential comes from (an environment variable name, never the value).
- The session-start read order: board and heartbeat, inbox, status, active work, audit queue.
