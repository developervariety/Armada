# Historical operator lead guidance

Retired. Do not use these instructions for current operation. See [archive status](README.md).

### 4.11 Autonomous lead cycles

The objective scheduler dispatches eligible objectives on its own. It does not
land work, close incidents, refill campaign lanes, or answer a helper. That
operator layer is `scripts/autonomy/lead-cycle.sh`, which runs ONE bounded pass
and exits.

Objective closeout evaluates each failed chain in an original voyage. Every
independent `Failed` or `LandingFailed` chain root needs a linked automatic
rescue whose missions all reached `Complete`. A completed rescue for one branch
does not cover a separate failed branch. Failed rescue attempts stay as history
and do not become new closeout obligations; a later completed attempt can
resolve the original failure. A missing linked voyage, a malformed mission
graph, unlanded work, or a cancelled-only original voyage keeps the objective
open.

```sh
scripts/autonomy/lead-cycle.sh run      # one cycle now; refuses if one is running
scripts/autonomy/lead-cycle.sh status   # running? and the last result
scripts/autonomy/lead-cycle.sh kill     # stop the running cycle
```

Two things start a cycle, and they are complementary:

- **The timer**, `scripts/autonomy/systemd/armada-lead-cycle.timer`, every hour.
  This catches work that arrives quietly, such as new objectives added while the
  fleet was idle and no event fired.
- **AgentWake**, when a mission outcome or a note addressed to the lead's key
  arrives. Set `remoteTrigger.agentWake.command` to
  `scripts/autonomy/lead-wake.sh`. That shim exists because Armada starts the
  configured command with the runtime's own flags in argv, including
  `--strict-mcp-config` with no `--mcp-config` -- which would give the woken
  process zero Armada tools -- and `--continue`, which resumes an unrelated
  session. The shim ignores argv, keeps only the wake text from stdin, and hands
  it to `lead-cycle.sh`.

`lead-cycle.sh` is single-flight. A timer tick arriving while a wake-started
cycle is running is refused, not queued, so one participant key never gets two
process owners.

**The lead runs only when nobody is watching.** `armada_lead_cycle_begin`
refuses with `operator-present: <keys> seen within N minutes` while any board
participant other than the lead itself (or a `helper-*` it started) has
heartbeated within `grokLead.operatorPresenceMinutes` (default 30; 0 disables
the gate). An interactive session, a dashboard viewer, and an Armada helper
session all count as an operator. The launcher records the refusal as
`skipped server-lease-refused` and exits, so a cycle that finds an operator
present costs one tool call. Measured before the gate: 131 cycles in 24 hours
while an operator session was live, 93% of their tool calls reads, and every
landing, closure and dispatch that mattered made by the operator.

Prefer `remoteTrigger.agentWake.deliveryMode = StoredWake` for the lead: a
directed note or mission outcome then waits on the board for the next timed
cycle instead of starting one. Process delivery (`Both`) started four cycles
for every timer tick and most of them re-triaged work an operator had already
closed.

**Cursor captains need `--approve-mcps`.** cursor-agent discovers a workspace
`.cursor/mcp.json` but leaves its servers "not loaded (needs approval)" in a
non-interactive `--print` run; `--trust` covers the workspace only. The runtime
passes `--approve-mcps` so the dock's Armada server loads. Proof is a Research
smoke mission whose report lists the Armada server by name; the file on disk
proves nothing by itself.

**One board, whatever the key says.** Every coordination tool resolves a blank
room key, `fleet`, and the literal word `default` to the one shared room
(`CoordinationRoom.NormalizeKey`). A client that reads "omit for the default
room" and sends the word `default` no longer creates a second room, which
split the board in two and hid the lead's handoffs from the completion gate.

The launcher also acquires Armada's durable `autonomy:lead-cycle` lease. This
prevents overlap with an external Grok lead. The systemd service requests
standby fallback. In `GrokPrimary` mode, Armada refuses that request until the
configured Grok inactivity period expires. The default is 130 minutes. In
`LegacyPrimary` mode, the existing lead runs normally.

The timer checks fallback eligibility once per hour. Therefore, a timer-only
fallback can start after the 130-minute threshold, not exactly at that time.
An AgentWake start uses the same shared check.

The Grok listener and its shared cycle controls are disabled by default. See
[Grok Bot Lead Integration](docs/autonomy/grok-bot-lead.md) before you enable them.

The shared lifecycle tools are:

- `armada_lead_cycle_status` reads the current mode and lease;
- `armada_lead_cycle_begin` requests one bounded cycle;
- `armada_lead_cycle_heartbeat` renews the active lease;
- `armada_lead_cycle_complete` verifies that every claim is released, posts the
  handoff to the shared board itself when the lead has not already posted the
  same text, records completion, and releases the lease. It never refuses on a
  wording or room mismatch: that refusal made the lead re-post and retry until
  one copy matched, which is where duplicate handoff notes came from. The lead
  passes its handoff to this tool and posts nothing itself;
- `armada_lead_cycle_fail` records an early stop or failure and releases the
  lease.

The timer and AgentWake must use the same state directory. The default is
`$HOME/.armada/autonomy-lead`. The Admiral container can write this bind mount,
and the host timer can read the same lock and log files. Do not use
`$HOME/autonomy-lead`: that host path is not mounted in the Admiral container,
so an AgentWake process cannot create it.

**Give the lead its own participant key.** `armada-lead` by default, and never an
interactive operator's key. Two process owners on one key duplicate dispatch and
cannot be told apart on the board.

An unattended cycle cannot ask a question. The prompt tells it to post an owner
decision to the board as a named item and carry on, rather than block. Read those
on your next session; they are the cycle's questions to you.

**Leave the timer running across a redeploy.** A tick that lands while the Admiral
is rebuilding preflights the MCP endpoint, records `admiral-unreachable`, and
exits without starting a cycle. Stopping the timer for a deploy needs somebody to
start it again afterwards, and that step gets missed: the lead sat idle for an
hour because a redeploy left it stopped. The skip is what makes the manual step
unnecessary. Stop it deliberately with `lead-cycle.sh kill`, or by disabling the
timer, and say so on the board because nothing else will notice.

**The timer is wall-clock and persistent.** `OnCalendar=hourly` with
`Persistent=true`, so the next run does not depend on the unit's activation
history, and a tick missed while the host was down runs at the next start. An
earlier monotonic schedule carried `Persistent=true` where it has no effect, so a
missed run was silently never caught up.

**The model is pinned.** The default runtime is Claude Code. It uses
`claude-fable-5` through the same Anthropic-compatible Vilao route as the Fable
judge captains. Claude Code and the provider control prompt caching for this
route.

Before you install the service, create the provider key file:

```sh
install -o armada -g armada -d -m 700 <service-home>/.armada/secrets
install -o armada -g armada -m 600 <secure-vilao-key-source> \
  <service-home>/.armada/secrets/autonomy-lead-vilao.key
```

The key file must contain only the Vilao API key. Do not put the key in the unit,
the repository, or the generated event log. The Claude Code launcher reads the
file and removes an inherited `ANTHROPIC_AUTH_TOKEN` before it starts.

**Each cycle leaves two files** under the lead's log directory:
`cycle-<stamp>.jsonl`, the whole event stream, and `cycle-<stamp>.log`, a rendered
digest of it. Read the digest; it lists what the cycle said, every tool it called,
whether each call worked, and how the run ended. A stream with no result event is
reported as `INCOMPLETE`, which is what a timeout looks like. `--print` alone emits
only the closing paragraph, which is how one eight-minute cycle left a 73-byte log
claiming it had nothing to report.

**The default cap is 30 minutes.** The prompt tells the cycle to reserve the last
three for its handoff and cleanup. Raise it with `AUTONOMY_LEAD_TIMEOUT_MIN`, and
keep the systemd unit's `TimeoutStartSec` above it as the outer backstop.

**The permission policy is the real boundary.** A headless run has nobody to
answer a permission prompt, so `lead-cycle.sh` writes the runtime policy before
it starts the model. It allows the primary agent to use Armada and ordinary file
and shell tools. It denies fleet-destructive and purge tools, deployment and
release tools, check resolution, the fleet-wide dispatch hold, AgentWake
registration, force push, Docker Compose, and systemd. Deny wins over allow. The
optional OpenCode agents have an additional read-only policy. Widen the policy
only for a named need.

**Send the Claude prompt on stdin.** `claude --mcp-config` is variadic,
so a positional prompt after it is consumed as a second config path and the run
dies with `MCP config file not found: <the entire prompt>`. OpenCode accepts the
prompt as its `run` argument.
