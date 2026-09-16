---
topic: "Connection And Discovery"
summary: "MCP transport, per-request authentication, tool pagination, and the start-of-session read sequence."
read_when: "Connecting a client to the Admiral, or starting an operator session."
applies_to: orchestrator
tier: leaf
---
# Connection And Discovery

The Admiral exposes stateless Streamable HTTP MCP at `/mcp`. `/rpc` is a
compatibility alias. An SSH stdio bridge can forward a local MCP client to a
loopback-bound remote Admiral. The bridge must connect to the running Admiral.
It must not start a second embedded Admiral process.

Every MCP request must carry a credential. A request without one gets `401`;
nothing falls back to a default administrative identity. Only a global
administrator sees the operator catalog. A launched captain authenticates with a
caller-scoped session token - a mission captain with the mission owner's token,
a chat captain with the caller's token - so a mission reaches only its owner's
records and no operator-only tool, never the admiral launch credential.
`docs/MCP_API.md` lists the caller rules, the captain credential scope and the
per-runtime headers.

Operator migration when an Admiral with MCP authentication is deployed:

1. On the server, write one header line to a protected file, for example
   `printf 'X-Api-Key: %s\n' "<admiral API key>" > ~/.armada/mcp-auth-header`,
   then `chmod 600` it. Never put the key in a board note, brief or shell history.
2. Set `ARMADA_MCP_AUTH_HEADER_FILE` to that absolute path in the `env` of every
   SSH stdio bridge entry. The bridge refuses requests until it is set.
3. Set `ARMADA_API_KEY` in the environment of every direct HTTP client. Entries
   written by `armada mcp install` reference it by name; re-run the install to
   update an entry written before this change, and add the header by hand to any
   entry you wrote yourself.
4. Refresh the Helm CLI with the Admiral image, then prove a read-only tool call
   through each client. Expect `401` from any client you did not update.
5. Captains need no change on supported runtimes; they receive the launch
   credential at launch. A Mux entry written by an earlier install has no
   `auth` object; re-run `armada mcp install` to add it.

Start each operator session with:

1. Call `armada_status`.
2. Call `armada_enumerate` with small pages for active voyages, missions,
   captains, merge entries, incidents, objectives, and checks.
3. Call `armada_drain_audit_queue` before new dispatches.
4. Read each relevant open objective in full.
5. Check incidents and the merge queue before you create more work.
6. Check `armada_unlanded_branches` when prior work can exist outside the
   normal landing path.
7. Call `armada_list_papercuts` to see the friction that recent captains
   reported. Section 4.9 gives the triage rules.

MCP `tools/list` is paginated. Follow `nextCursor` until it is absent. The
normal built-in catalog fits on one 500-tool page. Pagination remains active
for larger extension catalogs. A client that ignores `nextCursor` can hide
valid tools.

Supported captains receive the local MCP URL through runtime-appropriate dock
and launch configuration. Claude strict mode and Codex receive explicit launch
arguments because a project file alone is not sufficient for those paths. The
catalog also contains dispatch,
administration, deployment, restore, purge, and server-control actions. Those
tools stay outside normal captain scope; the operator owns them unless the
mission explicitly assigns the action. Set `seedDockRuntimeMcpConfig=false`
only when the deployment intentionally removes all Armada tools from captains.

`armada_enumerate` supports these entity types:

`fleets`, `vessels`, `captains`, `missions`, `voyages`, `docks`, `signals`,
`events`, `merge_queue`, `memories`, `personas`, `prompt_templates`, `pipelines`,
`playbooks`, `objectives`, `incidents`, `checks`, `releases`, and
`deployments`.

Use `pageSize` from 10 to 25 unless a larger page is necessary. Large text
fields are excluded by default. Request `includeDescription`, `includeContext`,
`includeTestOutput`, `includePayload`, or `includeMessage` only when needed.
