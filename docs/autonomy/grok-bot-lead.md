# Grok Bot Lead Integration

## 1. Executive conclusion

Armada now has a disabled-by-default foundation for a Grok Bot lead. It does
not expose the full Armada MCP catalog. It adds a separate, authenticated,
least-privilege MCP listener and one shared server-side cycle lease.

Do not enable `GrokLead` in production yet. First prove that the current Grok
Bot client can connect to a custom Streamable HTTP MCP server and can send a
bearer credential. Cursor's public Grok Bot documentation does not verify these
two functions.

`grok-bot-cli` does not remove this blocker. Its documented commands create and
update Bots and groups, send messages, and read threads. It uses the signed-in
macOS Grok Bot application. It does not document custom MCP registration,
routine creation, routine scheduling, or a Linux headless login.

The recommended first production design is hybrid. Grok Bot is the phone,
conversation, and notification surface. Armada remains the authority for
identity, permissions, overlap prevention, audit, and fallback.

## 2. Verified Grok Bot capabilities

The following facts are from current Cursor documentation, which is the vendor
documentation for this Grok Bot product:

- A Bot can keep context, use connected plugins, use a cloud computer, and run
  routines while the owner is away.
- The owner can send another message to steer or stop work.
- The iOS application uses the same account and cloud computer as desktop.
- Usage is a weekly allowance measured by work, agent steps, and tokens. The
  vendor does not publish a fixed number of routines per day.
- A routine can fail to run. The vendor tells the owner to check that it is
  enabled and that its schedule is correct. If it has no visible error for more
  than 24 hours, the owner must report a bug.
- Secrets must use the secure secret card. They must not be put in chat or
  ordinary files.
- The product is described as an early product.

Official pages:

- [Getting started with Grok Bot](https://cursor.com/help/grok-bot/getting-started)
- [Grok Bot on mobile](https://cursor.com/help/grok-bot/mobile)
- [Plans and billing](https://cursor.com/help/grok-bot/plans)
- [Store secrets securely](https://cursor.com/help/grok-bot/secrets)
- [Connect plugins](https://cursor.com/help/grok-bot/connect-plugins)

The following facts are unverified in public vendor documentation:

- custom remote MCP support;
- custom request headers or bearer-token injection for MCP;
- MCP OAuth support;
- Streamable HTTP and SSE compatibility with Armada;
- routine retry rules and a fixed daily routine limit;
- routine webhooks for completion, approval, and failure;
- access from the cloud computer to a private tunnel;
- durable export of all shell, browser, network, and tool-call logs;
- whether login state is isolated between Bots.

These are proof-of-concept gates. Do not infer them from the ability to connect
a vendor plugin.

Third-party source:

- [`grok-bot-cli`](https://github.com/ScriptedAlchemy/grok-bot-cli) documents
  Bot, group, message, and thread operations. It requires Node.js 18 or later,
  the Grok Bot macOS application, and one interactive sign-in. It reuses the
  application's encrypted session and routing credentials. This is useful for
  local message automation. It is not an Armada security boundary.

## 3. Current Armada lead contract

The existing lead is defined by:

- [lead-cycle.sh](../../scripts/autonomy/lead-cycle.sh);
- [lead-wake.sh](../../scripts/autonomy/lead-wake.sh);
- [lead-bootstrap-prompt.md](lead-bootstrap-prompt.md);
- [armada-lead-cycle.service](../../scripts/autonomy/systemd/armada-lead-cycle.service);
- [armada-lead-cycle.timer](../../scripts/autonomy/systemd/armada-lead-cycle.timer).

The timer is hourly. It uses `OnCalendar=hourly`. It is not every 30 minutes.
The 30-minute value is the maximum model cycle time. The systemd outer timeout
is 40 minutes.

The launcher keeps the local file lock and PID check. The new coordinator adds
the durable `autonomy:lead-cycle` lease. Both the legacy runner and Grok runner
must acquire this lease. The default lease is 40 minutes. A long Grok cycle
must renew it.

The server assigns the stable `armada-lead` participant identity to the Grok
listener. A client cannot select another identity. The server also uses that
identity for coordination reads, posts, claims, releases, and wake delivery.

The server records mode changes, cycle starts, heartbeats, completion,
failure, and each restricted MCP tool result as Armada events. Completion needs
a non-empty handoff that the same participant posted to the board during the
cycle. Completion is refused while that participant has an active claim. The
launcher treats a zero exit with an open server lease as a failed contract.

Control ownership is as follows:

| Control | Enforcement point |
| --- | --- |
| One local legacy process | launcher file lock and PID check |
| One lead across both runners | Armada durable lease |
| Stable Grok participant identity | restricted MCP listener |
| Grok bearer authentication | restricted MCP listener |
| Allowed Grok tools | server-side explicit allowlist |
| Write only during an active Grok cycle | server-side tool wrapper |
| Board handoff and claim release before completion | server-side lifecycle tool |
| Owner mode changes | admin-only Armada REST route |
| Legacy process timeout | launcher, then systemd outer timeout |
| Legacy shell and file permissions | model client policy and operating system |
| Public reachability and TLS | reverse proxy, firewall, and tunnel |

The old launcher deny rules remain useful for the local model. They do not
protect a cloud Bot. A cloud Bot can call any tool that a remote endpoint
advertises and accepts. Therefore, the remote endpoint must omit prohibited
tools and enforce identity and state on the server. A prompt or client-side
deny list is not an authorization control.

## 4. Architecture comparison

### A. Full replacement

Grok Bot connects directly to the restricted remote MCP listener. A routine
starts one cycle, reads Armada state, performs permitted coordination work,
posts a handoff, completes the cycle, and reports in its phone thread.

- Code: the restricted listener, cycle coordinator, lifecycle tools, audit
  events, and owner mode routes in this branch.
- Network: TLS ingress to only the restricted listener through an authenticated
  gateway. The normal MCP port stays private.
- Authentication: one rotated high-entropy bearer secret plus the fixed
  server-side participant identity.
- Risk: custom MCP and bearer behavior are not verified. Grok Bot's cloud
  computer is outside Armada's operating-system controls. A routine can also
  fail silently long enough for the legacy fallback threshold to expire.
- Recovery: the lease expires, the failed cycle remains in events, and the
  hourly legacy timer can take over after the configured inactivity period.
- Audit: Armada records accepted tool calls, but Grok Bot's complete internal
  reasoning and shell history are not verified as exportable.
- Production: no-go until all MCP, authentication, routine, and audit gates pass.

### B. Hybrid

Grok Bot is the owner-facing conversation and notification layer. A local
Armada service keeps execution and policy. `grok-bot-cli` can send bounded work
to a Bot and read the thread from the signed-in Mac. The Bot can send owner
messages back through the phone application. Armada still starts and controls
the real lead cycle.

- Code: add a small local bridge only after its message and failure behavior is
  tested. Do not put the Grok application session in Armada.
- Network: no public Armada MCP endpoint is necessary if the local bridge only
  exchanges messages.
- Authentication: the local CLI reuses the macOS application's session. Armada
  keeps its existing local trust boundary.
- Risk: the Mac application is a dependency. CLI behavior is third-party and
  can change. Messages are not the same as transactional commands.
- Recovery: systemd and AgentWake continue to run the legacy lead. A failed
  phone notification does not stop Armada work.
- Audit: Armada remains authoritative. Store every accepted owner instruction
  and every Bot summary as a board note or Armada event.
- Production: suitable after a narrow messaging proof of concept. This is the
  recommended first deployment.

### C. Grok CLI or API in the local wrapper

A local process calls an official xAI model API, or a verified headless client,
inside `lead-cycle.sh`. It keeps the existing repository checkout, shell,
network boundary, model policy, timeout, log renderer, AgentWake path, and
systemd schedule.

- Code: add a runtime adapter and its explicit policy. Keep the shared cycle
  tools that this branch adds.
- Network: outbound HTTPS to the model API. No inbound public MCP endpoint.
- Authentication: a server-side API key in a root-controlled secret file or
  service credential store.
- Risk: an API model does not automatically provide the Grok Bot phone thread,
  routine UI, or notifications. A model can still misuse any locally exposed
  tool, so server controls stay necessary.
- Recovery: current timeout, event log, timer, AgentWake, and fallback behavior
  remain available.
- Audit: strongest of the three choices because the local wrapper keeps the raw
  event stream and rendered digest.
- Production: technically suitable after an official supported headless path
  is selected. It does not meet the phone experience by itself.

## 5. Security and network design

The restricted listener binds to `127.0.0.1:7892` by default. Keep this bind.
Put a TLS reverse proxy or an authenticated tunnel in front of it. Publish only
one path to this listener. Do not proxy port 7891 or the normal `/mcp` endpoint.

Example Armada settings for an isolated test:

```json
{
  "GrokLead": {
    "Enabled": true,
    "Hostname": "127.0.0.1",
    "Port": 7892,
    "ParticipantKey": "armada-lead",
    "BearerTokenEnvironmentVariable": "ARMADA_GROK_MCP_TOKEN",
    "DefaultMode": "LegacyPrimary",
    "CycleLeaseMinutes": 40,
    "StandbyFallbackAfterMinutes": 130
  }
}
```

This setting only starts the loopback listener. It does not create a public
route, a credential, a tunnel, a Grok Bot, or a routine.

The external boundary must enforce:

- TLS 1.2 or later;
- a narrow source rule when the vendor supplies stable egress ranges;
- request and connection limits;
- a maximum request body;
- short upstream timeouts;
- no caching;
- no logging of the Authorization value;
- secret rotation and immediate revocation;
- denial of every path except the restricted MCP path.

Armada must enforce these controls even when the proxy is wrong:

- exact bearer-token comparison;
- fixed participant identity;
- an explicit tool allowlist;
- active-cycle checks before reversible tools;
- ownership checks before claim release;
- one durable cross-runner lease;
- a required completion handoff;
- durable lifecycle and tool-call events.

The current gateway categories are:

| Category | Tools |
| --- | --- |
| Read-only | status, enumerate, coordination read, campaign status, inbox, incident read, scheduler status, voyage status, AgentWake status |
| Reversible coordination | board post, heartbeat, own claim, signal, voyage nudge, mark signal read |
| Controlled write | cycle begin, cycle heartbeat, cycle complete, cycle fail |
| Owner-only | mode change through admin REST; all dispatch, objective mutation, incident mutation, release, deployment, purge, recall, hold, and server controls |

This first catalog cannot dispatch work, change objectives, close incidents,
deploy, release, purge, recall captains, or change the scheduler. This means it
cannot yet replace every action of the current lead. Add a controlled write
only after a named use case has a server-side invariant, an idempotency rule,
an audit event, and a recovery test.

## 6. Recommended proof of concept

1. Keep the durable mode at `LegacyPrimary` and keep `GrokLead.Enabled=false`.
2. In an isolated test environment, enable the loopback listener and provide
   `ARMADA_GROK_MCP_TOKEN` through the service secret environment.
3. Put a temporary TLS tunnel in front of port 7892. Confirm that port 7891 and
   all Armada REST routes are unreachable through it.
4. In Grok Bot, try to add the endpoint and secret. Record the exact connector
   fields and the actual HTTP requests. Stop if the client cannot send bearer
   authentication or cannot use Stateless Streamable HTTP.
5. Run ten read-only manual cycles. Test phone steering, stop messages,
   completion notifications, failures, lease expiry, and audit correlation.
6. Run scheduled read-only routines for seven days. Keep the hourly legacy
   timer enabled. Do not enable fallback takeover during this observation step.
7. Enable `GrokPrimary`. The legacy timer then acts as standby. It can acquire a
   fallback cycle only after 130 minutes without Grok mode or cycle activity.
   The timer checks once per hour, so a timer-only takeover can occur later than
   130 minutes. An AgentWake start also checks the same threshold.
8. Simulate an absent Grok routine and verify one legacy fallback cycle. Then
   return to `LegacyPrimary`.

Mode API:

```text
GET /api/v1/server/lead-control
PUT /api/v1/server/lead-control/mode
{"mode":"GrokPrimary"}
```

A mode change does not kill a cycle that already owns the lease. It changes
which runner can acquire the next cycle. Use `Maintenance` to refuse all new
cycles.

## 7. Go or no-go recommendation

- Hybrid phone and notification proof of concept: GO.
- Read-only direct MCP proof of concept in an isolated environment: GO after
  the owner supplies a test Grok Bot account and approves a temporary tunnel.
- Direct Grok Bot production replacement: NO-GO now.
- Direct exposure of the existing full Armada MCP endpoint: NO-GO.
- Removal of the old unattended lead: NO-GO. Keep it as the tested standby.

## 8. Open owner decisions

- Choose the first phone path: Grok Bot thread through the macOS CLI bridge, or
  direct Grok Bot custom MCP if the UI supports it.
- Choose the TLS or tunnel product for the isolated test endpoint.
- Choose the acceptable standby delay. The branch default is 130 minutes.
- Decide whether the first direct trial is read-only or can use reversible
  coordination tools.
- Decide which controlled Armada writes, if any, Grok can receive after the
  observation period.
- Choose the weekly Grok Bot usage plan and spend cap. There is no verified
  fixed daily routine count.
