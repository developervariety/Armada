# Retired autonomous lead integration

These files are retained as history. They are not supported deployment or operator instructions. Do not install the archived service, timer, gateway, or scripts.

The standalone lead launcher and Grok Bot integration are retired. The updated server source no longer exposes the Grok listener, OAuth proof-of-concept broker, lead-control REST routes, or lead-cycle MCP tools. The source and dedicated regression tests for those removed components remain available in Git history. An older deployed image can still contain these endpoints until it is replaced.

The objective scheduler, shared coordination board, generic AgentWake, bounded helper launcher, WebSocket watcher, and log renderer remain supported. Use the current [operator guide](../../armada-ops.md) for those tools.

Before updating an existing host, disable and remove its lead timer/service, remove the Grok gateway and listener configuration, and remove the lead-specific AgentWake target. Preserve unrelated wake clients and shared tools. Do not copy stored credentials into this archive. Old `grokLead` settings are no longer consumed by the server and must be removed from deployment configuration.

The nested directory layout preserves the former script and deployment paths for review. Historical links in the archived guides can refer to files outside this snapshot; use Git history to inspect those versions.
