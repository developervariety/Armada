# Plan: pi as an Armada agent runtime

Status: scoping plan only. No code has been changed. Paths marked (NEW) do not exist yet; every
other Armada path exists in the repository at commit dbef448c9. Paths that belong to the pi
repository (`docs/json.md`, `src/modes/print-mode.ts`, `examples/extensions/...`) are relative to
`packages/coding-agent/` in github.com/earendil-works/pi, and names such as `mcp.json`,
`models.json`, `auth.json`, `settings.json`, `SYSTEM.md` and `.pi/mcp.json` are pi configuration
files on the user's machine or in a working directory, not Armada repository files.

The OpenCode runtime is the reference implementation throughout, because it is the most recent
CLI runtime added to Armada and it touches almost every layer a new CLI runtime needs.

## Sources

Facts about pi were read from the pi repository (github.com/earendil-works/pi, `main` at commit
a92283056, 2026-10-27; package `packages/coding-agent`, npm `@earendil-works/pi-coding-agent`
v1.4.0, binary `pi`). The old name `badlogic/pi-mono` redirects to the same repository. The
pages below are under
`https://github.com/earendil-works/pi/blob/main/packages/coding-agent/`:

- `docs/quickstart.md` - install methods, Node requirement
- `docs/cli.md` and `src/cli/args.ts` (help text, around lines 300-345) - CLI flags
- `docs/json.md` and `src/modes/json-event.ts` - `--mode json` event stream
- `docs/message-types.md` - assistant message, usage, stopReason
- `docs/rpc.md`, `docs/rpc-commands.md`, `docs/rpc-extension-ui.md` - RPC mode
- `docs/sdk.md` - SDK
- `docs/configuration.md`, `docs/environment-variables.md`, `docs/providers.md`, `docs/models.md`
- `docs/mcp.md` - built-in MCP extension
- `docs/security.md`, `docs/extensions.md`, `examples/extensions/permission-gate.ts`
- `docs/session-format.md`, `docs/sessions.md`
- `docs/windows.md`
- `src/modes/print-mode.ts` (around lines 140-165) - exit codes

pi.dev/docs/latest is reachable but was not compared line by line with the repository copies;
the repository copies were treated as authoritative. Anything not confirmed from those files is
marked UNVERIFIED.

## 1. Summary of pi and why it fits

pi is an open-source, provider-neutral terminal coding agent from Earendil Inc. Facts relevant to
Armada:

- Install: `curl -fsSL https://pi.dev/install.sh | sh` (macOS/Linux),
  `powershell -c "irm https://pi.dev/install.ps1 | iex"` (Windows), or
  `npm install -g --ignore-scripts @earendil-works/pi-coding-agent`. Requires Node.js 22.19 or
  later (`engines.node >=22.19.0`). Updated with `pi update`.
- Headless: `pi -p "query"` (print mode). `--mode text|json|rpc`. `--mode json` emits strict JSONL
  on stdout (diagnostics on stderr) and exits when the prompts are done. `--mode rpc` is a
  long-lived JSONL command/event protocol on stdin/stdout.
- SDK: `createAgentSession()` in the same npm package (TypeScript only).
- Providers: Anthropic, OpenAI, Google, Azure, Bedrock, OpenRouter, Mistral, Groq, xAI,
  DeepSeek, and OpenAI-compatible endpoints such as Ollama through `models.json`
  (`providers.<id>.{baseUrl, api, apiKey, models}`). Keys come from environment variables
  (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, `AZURE_OPENAI_API_KEY` +
  `AZURE_OPENAI_BASE_URL`, `AWS_BEARER_TOKEN_BEDROCK`, ...) or `auth.json`.
- Config home: `~/.pi/agent` (override with `PI_CODING_AGENT_DIR`), holding `settings.json`,
  `models.json`, `auth.json`, `mcp.json`, `SYSTEM.md`, `APPEND_SYSTEM.md`, `extensions/`,
  `skills/`, `prompts/`. Projects have the same files under `.pi/`, loaded only after project
  trust.
- Context files: `AGENTS.md` / `AGENTS.override.md` / `CLAUDE.md` are loaded from the agent dir,
  parent directories and the cwd, without needing project trust (`-nc` disables them). Armada
  docks already carry `AGENTS.md` / `CLAUDE.md`, so mission context reaches pi with no work.
- MCP: built in, as the bundled extension `builtin:mcp`. Configured only through `mcp.json`
  (`{"mcpServers": {...}}`, stdio or `http`/`streamable-http` with `url` and `headers`, `${ENV}`
  expansion) or `pi mcp add`. Tools are named `mcp__<server>__<tool>`. `--no-mcp` disables it.
- Permissions: no approval prompts and no sandbox by default. Gating is done by extensions that
  handle the `tool_call` event and return `{ block: true, reason }`.
- Sessions: JSONL tree files under `~/.pi/agent/sessions/`; resume with `--session <path|id>`,
  pre-assign with `--session-id <id>`, or disable with `--no-session`.

Why it fits: pi's headless contract is the same shape Armada already drives for OpenCode, Codex
and Claude Code - one process per launch, prompt in, JSONL events out, exit. It reports exact
per-message token usage and cost, has first-class MCP over HTTP with header injection (so the
Armada MCP server and the session token work), exposes reasoning effort as a flag with a
superset of Armada's levels, and supports many providers, including local models, through one
binary. That gives Armada another provider-neutral captain next to OpenCode, with richer
telemetry.

## 2. Capability matrix

Values for existing runtimes come from the current adapters (`src/Armada.Runtimes/*Runtime.cs`,
`src/Armada.Core/Services/ReasoningEffortTranslator.cs`,
`src/Armada.Core/Services/CliPermissionPolicyResolver.cs`,
`src/Armada.Core/Services/MissionStreamDecoder.cs`,
`src/Armada.Core/Services/ChatTurnTelemetryRecorder.cs`). "Mission stream" means
`MissionStreamDecoder.SupportsRuntime` decodes it today.

| Capability | Claude Code | Codex | Gemini | Cursor | Mux | OpenCode | pi (proposed) |
|---|---|---|---|---|---|---|---|
| Headless mode | `claude -p` | `codex exec` | yes | `cursor-agent` | `mux` run | `opencode run` | `pi -p --mode json` |
| Streaming JSON | stream-json | `exec --json` | text | text | JSONL protocol | `--format json` | `--mode json` JSONL |
| Mission stream decode | yes | yes | no | no | yes | no | yes (Phase 2) |
| Token telemetry | result event (+cost) | turn.completed | estimated | estimated | run_completed | step_finish (+cost) | message_end usage (+cost) |
| Armada MCP binding | isolation plan | `-c` overrides | workspace settings | project mcp.json | `--mcp-config` | `OPENCODE_CONFIG_CONTENT` | project `.pi/mcp.json` (see 3.3) |
| Bypass flag | `--dangerously-skip-permissions` | full-auto/bypass | yolo | `--force` | `--yolo` | `--auto` | default behavior (no prompts) |
| Refuse | acceptEdits | non-bypass | auto_edit | no `--force` | no `--yolo` | no `--auto` | `--exclude-tools bash` (see 3.4) |
| ApproveInArmada | yes (`--permission-prompt-tool`) | falls back to Refuse | falls back | falls back | yes (Mux 1.5+) | falls back | falls back first; extension later (Phase 6) |
| Reasoning effort | thinking-token env | `ToCodexReasoningEffort` | none | none | `ToMuxEffort` | `--variant` minimal/high | `--thinking off..high` (1:1) |
| Model selection | `--model` | `--model` | `--model` | `--model` | endpoint | `--model provider/id` | `--model provider/id` (+`--provider`) |
| Resume (`SupportsResume`) | true | false | false | false | false | false | false at first; `--session-id` makes true possible |
| Prompt via stdin | yes | yes | yes | yes | n/a | yes | UNVERIFIED (see 3.1) |
| Windows | yes | yes | yes | yes | yes | yes (npm .cmd) | yes, install.ps1; bash tool needs Git Bash |

## 3. Design

### 3.1 Command building

New `src/Armada.Core/Services/PiCommandBuilder.cs` (NEW), modeled on
`src/Armada.Core/Services/OpenCodeCommandBuilder.cs`:

```
pi -p --mode json --no-session [--approve]
   [--model <provider/id>] [--thinking <level>]
   [--exclude-tools bash]          (Refuse policy only)
   [--append-system-prompt <text>] (only if Armada needs it; AGENTS.md already reaches pi)
```

- Working directory: pi has no `--cwd`/`--dir` flag; the process working directory decides
  context files, project config and `@path` resolution. `BaseAgentRuntime` already sets
  `ProcessStartInfo.WorkingDirectory`, so the builder takes no directory argument (unlike
  OpenCode's `--dir`). It still validates `workingDirectory` and `prompt` and throws
  `ArgumentNullException` like the OpenCode builder.
- Model: pass `Captain.Model` as `--model` unchanged (pi accepts `provider/id` and a
  `:<thinking>` suffix). `--thinking` overrides the suffix, so effort stays authoritative.
- Reasoning effort: new `ReasoningEffortTranslator.ToPiThinkingLevel` in
  `src/Armada.Core/Services/ReasoningEffortTranslator.cs`: Off->`off`, Minimal->`minimal`,
  Low->`low`, Medium->`medium`, High->`high`, null->omit. pi clamps to what the model supports.
  pi's extra `xhigh`/`max` are not reachable from Armada and need no mapping.
- Sessions: `--no-session` for missions, Ask turns and planning. Planning sessions relaunch
  against Armada's preserved transcript (see `AgentRuntimeCapabilities`), so pi session files are
  not needed and would otherwise accumulate in `~/.pi/agent/sessions`. UNVERIFIED: whether the
  JSON-mode session header line is still emitted with `--no-session`; the parser must not depend
  on it.
- Prompt delivery: every CLI adapter except Mux sets `UsePromptStdin => true` to avoid Windows
  cmd.exe truncating multi-line arguments to npm `.cmd` wrappers. UNVERIFIED: whether `pi -p`
  reads the prompt from redirected stdin when no positional message is given. If it does not,
  write the prompt to a temp file and pass `@<file>` (pi resolves `@path` arguments), deleting the
  file after exit. Task 2.1 must settle this with a real run before the builder is final.
- Turn limit: pi has no `--max-turns` (grep of `src` found none; UNVERIFIED that no other limit
  exists). Armada's existing mission timeout and `StopAsync` remain the only bound.

### 3.2 Stream parsing

`--mode json` record types (from `docs/json.md`):

- `session` header `{type, version, id, timestamp, cwd}` (first line when sessions are on).
- `agent_start`, `agent_end{messages, willRetry}`, `agent_settled{aborted}` (true end of run).
- `turn_start`, `turn_end{message, toolResults}`.
- `message_start{message}`, `message_update{usage, assistantMessageEvent}`,
  `message_end{message}` (authoritative final message). `assistantMessageEvent.type` is
  `text_start|text_delta|text_end`, `thinking_*`, `toolcall_start{id,toolName}`,
  `toolcall_delta`, `toolcall_end{toolCall}`, `done`, `error{reason,error}`.
- `tool_execution_start{toolCallId,toolName,args}`, `tool_execution_update`,
  `tool_execution_end{toolCallId,toolName,result,isError,durationMs}`.
- `auto_retry_start{attempt,maxAttempts,delayMs,errorMessage}`,
  `auto_retry_end{success,attempt,finalError?}`, `compaction_start/end`, `queue_update`, and
  others that Armada ignores.
- Usage: `{input, output, cacheRead, cacheWrite, cacheWrite1h?, reasoning?, totalTokens,
  cost:{input,output,cacheRead,cacheWrite,total}}`. `reasoning` is already included in `output`.
  On `message_update` it is cumulative for the current response and may be zero until completion;
  on `message_end` it is final.
- Assistant message: `stopReason` in `pending|stop|length|toolUse|error|aborted|deferred`, plus
  `errorMessage?`, `provider`, `model`, `responseModel?`.

Framing rule: split on LF only and strip a trailing CR (pi documents that U+2028/U+2029 are not
record boundaries). Armada's line reader in `BaseAgentRuntime` already reads by line;
`RuntimeStreamJson` must accept the line as a single JSON object.

Typed models, following the repository rule of no `JsonElement` access and one class per file
(`src/Armada.Core/Protocol/OpenCodeStreamEvent.cs` and friends are the pattern):

- `src/Armada.Core/Protocol/PiStreamEvent.cs` (NEW) - top-level record; `TryParse(line, out evt)`
  built on `src/Armada.Core/Protocol/RuntimeStreamJson.cs`; constants for the type names.
- `src/Armada.Core/Protocol/PiAssistantMessageEvent.cs` (NEW) - nested delta event.
- `src/Armada.Core/Protocol/PiMessage.cs` (NEW) - role, content blocks, stopReason,
  errorMessage, provider, model, usage.
- `src/Armada.Core/Protocol/PiContentBlock.cs` (NEW) - text / thinking / toolCall blocks (use
  `src/Armada.Core/Protocol/TextOrRawJsonConverter.cs` or `RawJsonStringConverter.cs` for
  free-form `args` and `result`).
- `src/Armada.Core/Protocol/PiUsage.cs` and `src/Armada.Core/Protocol/PiCost.cs` (NEW).

Consumers:

- Final message: pi in JSON mode exits 0 even on provider errors (`print-mode.ts` sets exit 1 for
  error/aborted/length only in text mode). `PiRuntime` therefore keeps the last assistant
  `message_end` and, in `ResolveExitCode`, writes its text to the final-message file and returns a
  nonzero code when the last `stopReason` is `error`, `aborted` or `length`, or when the stream
  ended without `agent_end`/`agent_settled` (EOF). A new helper
  `src/Armada.Runtimes/PiFinalMessage.cs` (NEW) replaces the
  `src/Armada.Runtimes/CliTextFinalMessage.cs` role used by OpenCode.
- Provider errors: `RuntimeProviderErrorParser.TryParsePiJsonLine` (new method in
  `src/Armada.Core/Services/RuntimeProviderErrorParser.cs`) classifies `message_end` with
  `stopReason=error`, `assistantMessageEvent.type=error`, and `auto_retry_end{success:false}` using
  the existing explicit-signal rules (HTTP status in `errorMessage`, never substring guessing on
  narration). Stderr keeps `TryParseCliStderrLine(line, "Pi")`. A retry that later succeeds
  (`auto_retry_start` then normal completion) must not be reported as a failure.
- Telemetry: `ChatTurnTelemetryRecorder.ObservePi` sums usage from each assistant `message_end`
  (one per model response; a turn with tool calls has several). It must not add `reasoning` to
  output again and must not sum `message_update` usage (cumulative, would double count).
- Log formatting: `RuntimeLogFormatter.TryFormatPiEvent` shows `text_delta` joined text,
  `tool_execution_start` as a tool line, `tool_execution_end` with `isError`, and drops
  `message_update` usage noise.
- Mission streaming: `src/Armada.Core/Services/PiMissionStreamDecoder.cs` (NEW), registered in
  `MissionStreamDecoder.SupportsRuntime` / factory, modeled on `MuxMissionStreamDecoder.cs`
  (join deltas into lines).
- Tool wait tracking: `RuntimeToolWaitTracker` gains a pi branch keyed on
  `tool_execution_start` / `tool_execution_end` `toolCallId`.
- Ask tool calls: `src/Armada.Server/Ask/ToolCallCollector.cs` can use pi's `durationMs` instead
  of the first-seen clock used for Codex/OpenCode.

### 3.3 MCP config injection

pi has no per-invocation MCP flag (no `--mcp-config`, no config-content environment variable).
It reads `~/.pi/agent/mcp.json` and, for trusted projects, `.pi/mcp.json`, where a project entry
replaces a user entry of the same name. Header values support `${ENV}` expansion.

Recommendation (same strategy as Cursor's project `.cursor/mcp.json` in
`CaptainThreadMcpPlanner`):

- Write `<workingDirectory>/.pi/mcp.json` containing `armada` (and every host Armada alias found
  in `~/.pi/agent/mcp.json`, redefined as the scoped server or `"enabled": false`) with
  `"type": "http"`, the Admiral MCP URL, and `"headers": {"X-Token": "${ARMADA_MCP_TOKEN}"}`. The
  token travels only in `CaptainThreadMcpPlanner.TokenEnvironmentVariable`, never on disk or argv.
- Pass `--approve` so the headless run trusts the project and loads `.pi/mcp.json` (headless runs
  otherwise skip untrusted project config, `defaultProjectTrust` "ask").
- For Ask turns the working directory is the throwaway turn directory, so nothing leaks. For
  missions the directory is a dock (git worktree): add `.pi/mcp.json` to the worktree's
  `info/exclude` before launch so it is never committed, and delete it after exit.
- `PiRuntime.PopulateHostMcpConfiguration` reads `~/.pi/agent/mcp.json` (or
  `$PI_CODING_AGENT_DIR/mcp.json`) into a new `CaptainThreadMcpPlanRequest.HostPiMcpConfigDocuments`
  list; `HostMcpConfigDocument` already models an `mcpServers` map.
- Isolation: `CaptainLaunchIsolationPlanner.CarriesSessionToken(Pi)` returns false (like
  OpenCode), so full-isolation token launches fall back to the per-invocation plan above.

Alternative considered: a per-launch `PI_CODING_AGENT_DIR` overlay with copied
`settings.json`/`models.json` and a generated `mcp.json`. Rejected for now because `auth.json`
holds OAuth tokens that pi rewrites on refresh; copying it loses refreshes and symlinking it is
UNVERIFIED (an atomic rename would break the link). It hides the user's login, which
`CaptainThreadMcpPlanner` explicitly avoids.

Risk of `--approve`: it also loads the repository's own `.pi/extensions` and `.pi/settings.json`.
A captain already runs repository code through bash, so this is not a new trust boundary, but it
must be documented in `docs/INSTRUCTIONS_FOR_PI.md` (NEW).

### 3.4 Permission policy mapping

pi never prompts; there is no flag to turn prompts on. Mapping of `CliPermissionPolicyEnum`:

- Bypass: default pi behavior (all built-in tools). This matches `--auto`/`--yolo` elsewhere and
  stays admin-only through `CliPermissionService`.
- Refuse: Claude Code's Refuse is `acceptEdits` in print mode - file edits are allowed, shell and
  other prompting tools are refused. The pi equivalent is `--exclude-tools bash` (keeps `read`,
  `edit`, `write` and MCP tools; `--tools` would drop MCP tools only if an entry starts with
  `mcp__`, which Armada will not pass). Captain `AutoApprove=false` maps to Refuse, as for OpenCode.
- ApproveInArmada: Phases 1-5 leave `CliPermissionPolicyResolver.SupportsPromptHook(Pi)` and
  `PromptsOverMcp(Pi)` false, so the resolver's existing `ApplyFallback` downgrades to Refuse with
  `CliPermissionFallbackReasonEnum.RuntimeUnsupported` and a user-visible note. No new refusal path is needed.
- Phase 6 (optional): ship an Armada extension, `src/Armada.Runtimes/Pi/armada-permission-gate.ts`
  (NEW), loaded with `--extension <path>`. Its `tool_call` handler calls the Armada MCP tool
  `cli_permission_prompt` over the same scoped MCP connection (or the REST equivalent with
  `ARMADA_MCP_TOKEN`), awaits the decision, and returns `{ block: true, reason }` on deny. Then
  `SupportsPromptHook`/`PromptsOverMcp` return true for pi and `CliPermissionPromptSetup` gets a pi
  branch. UNVERIFIED: that an async `tool_call` handler can block for minutes while a human
  decides, and how pi times that out. The extension must fail closed (block) on any error, as
  `examples/extensions/permission-gate.ts` does when `ctx.hasUI` is false.

Armada's own MCP tools stay governed by Ask proposals and per-tool authorization regardless of
the CLI policy.

### 3.5 Harbor detection

- `src/Armada.Core/Harbor/HarborRuntimeDetector.cs`: `CommandFor(Pi) => "pi"`.
- `src/Armada.Core/Services/RuntimeDetectionService.cs`: add `pi` to `DetectAllRuntimes` and the
  install hint ("Install pi (https://pi.dev) and ensure the `pi` command is on PATH"). Note that
  OpenCode is missing from this service today; adding pi should not silently change the default
  order of `DetectDefaultRuntime` (append pi last).
- `src/Armada.Core/Models/HarborCapability.cs`: doc comment examples list "pi".
- Optional readiness: `pi auth check` exits 0 ready, 1 not ready, 2 invalid. Not required for
  detection; candidate for a later doctor/health check.
- Name collision risk: `pi` is a short command name. UNVERIFIED whether other tools install a
  `pi` binary on common systems; detection could confirm with `pi --version` output.

### 3.6 Print + JSON mode versus RPC mode

Recommendation: use print + `--mode json` for every Armada launch (missions, Ask turns, planning).

- It matches `BaseAgentRuntime`'s one-process-per-launch lifecycle, process ledger, `StopAsync`,
  Harbor remote execution (`RemoteAgentRuntime`, `LocalHarborJobRunner`) and the existing stdout
  line pipeline. No new long-lived process management is needed.
- The event shapes are the same as RPC mode, so the parser written now is reusable.
- RPC mode adds steering, `abort`, `get_session_stats`, and `extension_ui_request` (a possible
  native approval bridge with `ctx.hasUI=true`). Those matter only for a future interactive or
  steerable captain; revisit RPC if Armada adds mid-mission steering or if the Phase 6 extension
  needs `ctx.ui` dialogs. Track as an open question, not in this plan's scope.
- The SDK is TypeScript-only and would require a Node sidecar; not recommended.

## 4. Ordered task list

Each phase is sized to be one Armada mission. Tasks inside a phase are in order.

### Phase 1 - Runtime enum, adapter and command line (headless missions work)

1.1 Add `Pi` to `src/Armada.Core/Enums/AgentRuntimeEnum.cs` with `[EnumMember(Value = "Pi")]`,
    appended after `Custom`. Captain, persona-priority, stage-outcome and CLI-permission rows
    store the runtime as its name (`Runtime.ToString()` / `Enum.Parse`, for example
    `src/Armada.Core/Database/Postgresql/Implementations/CaptainMethods.cs`), so no migration is
    needed; appending last also keeps any ordinal use (for example a stable sort) unchanged.
    Acceptance: a Pi captain round-trips through the database suites on SQLite.
1.2 `src/Armada.Core/Services/AgentRuntimeParser.cs`: accept `pi`, `Pi`, `pi-coding-agent`;
    update the help string. Acceptance: parser unit cases for each alias; unknown values still
    rejected.
1.3 `src/Armada.Core/Services/ReasoningEffortTranslator.cs`: add `ToPiThinkingLevel`.
    Acceptance: all five levels plus null.
1.4 Create `src/Armada.Core/Services/PiCommandBuilder.cs` (NEW) per section 3.1. Acceptance:
    exact argument lists for default, model+effort, Refuse (`--exclude-tools bash`), empty
    working directory or prompt throws.
1.5 Create `src/Armada.Runtimes/PiRuntime.cs` (NEW) deriving `BaseAgentRuntime`: `Name => "Pi"`,
    `SupportsResume => false`, `RuntimeType => Pi`, `GetCommand` via `ResolveExecutable`,
    `BuildArguments`, prompt delivery per the 3.1 decision, `AutoApprove`/`ExecutablePath` like
    `OpenCodeRuntime.cs`. Register in `src/Armada.Runtimes/AgentRuntimeFactory.cs`.
1.6 `src/Armada.Core/Services/AgentRuntimeCapabilities.cs`: add Pi to `PlanningSessionRuntimes`.
1.7 `src/Armada.Core/Harbor/HarborRuntimeDetector.cs` and
    `src/Armada.Core/Services/RuntimeDetectionService.cs` per 3.5;
    `src/Armada.Core/Models/HarborCapability.cs` doc comment.
1.8 Runtime switches that must include Pi: `src/Armada.Runtimes/RemoteAgentRuntime.cs` (provider
    error routing, temporary stderr-only like OpenCode),
    `src/Armada.Core/Services/CaptainTierSelector.cs` (Pi defaults like OpenCode),
    `src/Armada.Core/Services/HandoffReaderResolver.cs` (Pi reads files and has MCP),
    `src/Armada.Core/Services/CaptainLaunchIsolationPlanner.cs` (`CarriesSessionToken` false, doc
    comment), `src/Armada.Core/Enums/CliPermissionPolicyEnum.cs` and
    `src/Armada.Core/Models/CaptainApprovalOptions.cs` doc comments.
Acceptance for the phase: `dotnet build src/Armada.sln` clean; a captain with runtime `Pi` can be
created over REST and MCP; a mission with a stub `pi` executable launches with the expected
arguments (see Phase 5 tests that land with this phase).

### Phase 2 - JSON stream parsing, final message, errors, telemetry

2.1 Run a real `pi -p --mode json` on macOS and Windows; capture fixtures for: plain reply, a
    turn with `bash` + `edit` tool calls, an MCP tool call, a provider auth error (bad key), an
    abort (SIGTERM), a `length` stop, and an auto-retry. Decide stdin versus `@file` prompt
    delivery (3.1) and update `PiRuntime` accordingly.
2.2 Create the protocol models in `src/Armada.Core/Protocol/` (NEW `PiStreamEvent.cs`,
    `PiAssistantMessageEvent.cs`, `PiMessage.cs`, `PiContentBlock.cs`, `PiUsage.cs`,
    `PiCost.cs`); update the doc comment in `src/Armada.Core/Protocol/RuntimeStreamJson.cs`.
    Acceptance: every fixture line parses or is ignored; malformed JSON and non-object values
    return false.
2.3 Create `src/Armada.Runtimes/PiFinalMessage.cs` (NEW) and wire `BeginLaunch`,
    `ResolveExitCode`, `TryParseProviderError` in `PiRuntime`. Acceptance: final-message file
    holds the last assistant text; `stopReason` error/aborted/length and missing `agent_end`
    produce a nonzero code even though pi exited 0.
2.4 `src/Armada.Core/Services/RuntimeProviderErrorParser.cs`: `TryParsePiJsonLine`; route stdout
    lines for Pi through it in `PiRuntime` and `src/Armada.Runtimes/RemoteAgentRuntime.cs`.
    Acceptance: 401/429/5xx fixtures classify correctly; a recovered auto-retry does not.
2.5 `src/Armada.Core/Services/ChatTurnTelemetryRecorder.cs` (`ObservePi`) and
    `src/Armada.Core/Models/CaptainChatMetrics.cs` doc comment;
    `src/Armada.Core/Services/MissionService.Completion.cs` uses the runtime usage report for Pi.
    Acceptance: multi-response fixture sums input/output/cache and cost exactly; reasoning not
    double counted.
2.6 `src/Armada.Core/Services/RuntimeLogFormatter.cs` (`TryFormatPiEvent`).
2.7 Server consumers: `src/Armada.Server/CaptainChatService.cs` (Ask streaming: text deltas,
    thinking, tool use, telemetry), `src/Armada.Server/PlanningSessionCoordinator.cs` (strip
    protocol lines, keep assistant text; add `PiRuntime.IsProtocolEventLine` /
    `TryExtractAssistantText` helpers like OpenCode's), `src/Armada.Server/Ask/ToolCallCollector.cs`
    (`durationMs`).
2.8 `src/Armada.Core/Services/MissionStreamDecoder.cs` plus
    `src/Armada.Core/Services/PiMissionStreamDecoder.cs` (NEW);
    `src/Armada.Core/Services/RuntimeToolWaitTracker.cs` pi branch.

### Phase 3 - MCP binding and permission policy

3.1 `src/Armada.Core/Services/CaptainThreadMcpPlanner.cs`: Pi strategy per 3.3 (project
    `.pi/mcp.json`, alias disabling, `--approve`); `src/Armada.Core/Services/CaptainThreadMcpPlanRequest.cs`:
    `HostPiMcpConfigDocuments`. `src/Armada.Core/Models/HostMcpConfigDocument.cs` doc comment.
3.2 `PiRuntime.PopulateHostMcpConfiguration` reads `~/.pi/agent/mcp.json` /
    `$PI_CODING_AGENT_DIR/mcp.json`; mission launches add `.pi/mcp.json` to the worktree exclude
    file and remove it after exit (hook location in `src/Armada.Runtimes/BaseAgentRuntime.cs`).
    `src/Armada.Core/Settings/McpSettings.cs` doc comment.
3.3 `PiCommandBuilder` Refuse mapping and `src/Armada.Core/Services/CliPermissionPolicyResolver.cs`
    (no prompt hook, so ApproveInArmada falls back to Refuse with RuntimeUnsupported).
    `src/Armada.Runtimes/CliPermissionPromptSetup.cs` unchanged until Phase 6.
Acceptance: a thread-scoped Ask turn on a pi captain can call `mcp__armada__status` with the
session token; the token is not in argv or on disk; a host `armada` entry in
`~/.pi/agent/mcp.json` is not used; the dock's `git status` is clean after the mission.

### Phase 4 - Clients, CLI, TUI, dashboard, docs

4.1 Helm: `src/Armada.Helm/Commands/McpClientKindEnum.cs` (`Pi`),
    `src/Armada.Helm/Commands/McpConfigHelper.cs` (`GetPiConfigPath` = `~/.pi/agent/mcp.json`
    honoring `PI_CODING_AGENT_DIR`, `IsPiAvailable`, local and remote targets writing
    `mcpServers.armada`), `src/Armada.Helm/Commands/McpInstallCommand.cs` (target list and plain-
    text-token note), `src/Armada.Helm/Commands/CaptainAddSettings.cs` and
    `src/Armada.Helm/Commands/CaptainUpdateSettings.cs` (runtime help text).
4.2 TUI: `src/Armada.Tui/Screens/Build/CaptainForm.cs` (`Runtimes`, `SupportsAutoApprove`,
    label), `src/Armada.Tui/Ask/AskController.cs` (`INSTRUCTIONS_FOR_PI.md`).
4.3 Server MCP schemas: `src/Armada.Server/Mcp/Tools/McpCaptainTools.cs` and
    `src/Armada.Server/Mcp/Tools/McpStageOutcomeTools.cs` runtime descriptions.
4.4 Dashboard: `src/Armada.Dashboard/src/lib/captainForm.ts` (option and tooltip),
    `src/Armada.Dashboard/src/lib/captainApproval.ts`, `src/Armada.Dashboard/src/lib/askCaptain.ts`,
    `src/Armada.Dashboard/src/pages/Captains.tsx`, `src/Armada.Dashboard/src/pages/CaptainDetail.tsx`,
    `src/Armada.Dashboard/src/types/models.ts` (cost comment). The tooltip string is an i18n key:
    update it in all eight locales of `src/Armada.Server/wwwroot/i18n/armada.json` in the same
    commit, or the catalog tests fail.
4.5 Docs: `docs/INSTRUCTIONS_FOR_PI.md` (NEW) and `docs/PI_AS_ORCHESTRATOR.md` (NEW) modeled on
    the OpenCode pair; runtime lists in `docs/CAPTAINS.md`, `docs/CAPTAIN_ROUTING.md`,
    `docs/MCP_API.md`, `docs/REST_API.md`, `docs/MODEL_GUIDANCE.md`, `docs/MODEL_HETEROGENEITY.md`,
    `docs/HARBOR_AWARE_TOOL_PROBE.md`, `docs/REMOTE_SERVER.md`, `docs/SECURITY_REVIEW.md`,
    `docs/TESTING.md`, `docs/UPGRADING.md`, `docs/WEBSOCKET_API.md`, `docs/ASK_ARMADA_HOME_BASE.md`;
    `README.md`, `GETTING_STARTED.md`, `FAST_TRACK_SETUP.md`, `DOCKERHUB_README.md`,
    `CLAUDE.md` / `AGENTS.md` architecture line, and `CHANGELOG.md` (`## Unreleased`).
4.6 `Armada.postman_collection.json`: the captain create description lists runtimes (and already
    omits Mux and OpenCode); add them and Pi. Edit as bytes (the file is CRLF with LF blocks).
4.7 API surface: MCP schema description changes may alter `docs/api-surface-1.0.json` /
    `docs/API_SURFACE_1.0.md`; regenerate with `scripts/common/generate-api-surface.sh -f net10.0`
    if E2E.ApiContract reports a change.
4.8 Installers / Harbor app: no runtime is bundled by the installers today (grep of `scripts`,
    `docker`, `factory`, `publisher.json` finds no runtime CLI installation), so nothing to add;
    confirm the Harbor tray's runtime list comes from `HarborRuntimeDetector`.

### Phase 5 - Tests (land each part with its phase)

See section 5. Phase 1 lands the builder/parser/factory tests, Phase 2 the fixture tests,
Phase 3 the MCP/permission tests, Phase 4 the dashboard tests.

### Phase 6 (optional) - ApproveInArmada through a pi extension

6.1 `src/Armada.Runtimes/Pi/armada-permission-gate.ts` (NEW) per 3.4, copied to the bin
    (csproj item; edit the `.csproj` as bytes, CRLF).
6.2 `CliPermissionPolicyResolver.SupportsPromptHook`/`PromptsOverMcp` return true for Pi;
    `CliPermissionPromptSetup.cs` adds `--extension <path>` to the launch.
6.3 Tests: allow, deny, Admiral unreachable (must block), timeout.

## 5. Test plan

Test project: suites in `src/Test.Shared` (discovered by reflection), run with
`dotnet run --project src/Test.Automated --framework net10.0 -- --suites <prefix>`; the same
suites run under `src/Test.Xunit` and `src/Test.Nunit`. Dashboard tests run with vitest in
`src/Armada.Dashboard`.

Unit tests (no pi needed):

- `src/Test.Shared/Suites/Services/PiRuntimeSuite.cs` (NEW, suite id `Services.PiRuntime`),
  modeled on `src/Test.Shared/Suites/Services/OpenCodeRuntimeSuite.cs`: builder arguments,
  effort mapping, parser aliases, protocol-line detection, assistant text extraction, final
  message and exit-code resolution for each stopReason, EOF without `agent_end`.
- `src/Test.Shared/Infrastructure/PiStreamFixtures.cs` (NEW): verbatim captured lines from task
  2.1 (record the pi version in a comment). Example of the shape, from `docs/json.md`:
  `{"type":"message_update","usage":{"input":100,"output":1,"cacheRead":0,"cacheWrite":0,"totalTokens":101,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"Hello "}}`
- Extend existing suites with Pi cases: `RuntimeProtocolParsingSuite.cs`,
  `RuntimeProviderErrorParserSuite.cs`, `ChatTurnTelemetryRecorderSuite.cs`,
  `CaptainThreadMcpPlannerSuite.cs`, `CliPermissionPolicySuite.cs` (ApproveInArmada falls back to
  Refuse; Refuse adds `--exclude-tools bash`), `HarborRuntimeDetectionSuite.cs`,
  `CaptainTierSuite.cs`, `McpClientHostSuite.cs`, `HelmInputSuite.cs`, `SecurityHardeningSuite.cs`
  (token never in argv), all under `src/Test.Shared/Suites/Services/`;
  `src/Test.Shared/Suites/Runtimes/AgentRuntimeFactorySuite.cs`,
  `src/Test.Shared/Suites/Runtimes/ThreadScopedLaunchSuite.cs`,
  `src/Test.Shared/Suites/Runtimes/MissionStreamingSuite.cs`,
  `src/Test.Shared/Suites/Models/CaptainModelSuite.cs`,
  `src/Test.Shared/Suites/Database/HarborDatabaseSuite.cs`.
- Stub executable: extend `src/Test.Shared/Infrastructure/StubAgentProcesses.cs` with a stub
  `pi` that replays fixture lines and records argv, stdin and environment, so launch tests assert
  the real command line and that `.pi/mcp.json` contains `${ARMADA_MCP_TOKEN}` rather than the
  token. Test helpers for the generated file: `src/Test.Shared/Infrastructure/PiMcpConfigFile.cs`
  (NEW), like `OpenCodeConfigFile.cs`.
- Dashboard: vitest cases for `captainForm.ts`, `captainApproval.ts`, `askCaptain.ts`; the
  existing i18n catalog tests cover the tooltip.

Integration smoke test:

- `src/Test.Shared/Suites/Runtimes/PiRuntimeSmokeSuite.cs` (NEW, suite id `Runtimes.PiSmoke`):
  skipped when `pi` is not on PATH or `pi auth check` is not 0. Runs a real `PiRuntime` in a temp
  git repo with prompt "create hello.txt containing hi and reply DONE", asserts exit 0, the file,
  the final message, and nonzero token counts. A second case runs with a deliberately bad API key
  (`--api-key` or env) and asserts the provider error is classified and the exit code is nonzero.
- Manual check per release on Windows (install.ps1, Git Bash present) since CI has no pi.

Gates to run: `Services.PiRuntime`, `Runtimes`, `Services.CliPermission`, `Services.Captain`,
`E2E.ApiContract`, `E2E.Authorization`, `Tui`, `Client`, dashboard `npm run build` and
`npm run test:run`.

## 6. Open questions and risks

- UNVERIFIED: `pi -p` reading the prompt from stdin (affects Windows; fallback is `@file`).
- UNVERIFIED: JSON-mode session header with `--no-session`; parser must not depend on it.
- UNVERIFIED: context-file precedence across agent dir, parents and cwd (pi docs do not spell it
  out). Armada relies only on the dock's `AGENTS.md`/`CLAUDE.md` being loaded.
- UNVERIFIED: no turn limit besides Armada's timeout.
- UNVERIFIED: an extension `tool_call` handler can await a long human decision (Phase 6).
- UNVERIFIED: behavior on Windows beyond `docs/windows.md` (bash tool requires Git Bash; an
  optional `powershell` tool exists).
- Risk: JSON mode exits 0 on provider errors; if the exit-code override in 2.3 is missed, failed
  missions look successful.
- Risk: `--approve` loads repository-supplied `.pi/extensions` (repository code running in the
  pi process). Same trust as bash, but must be documented; alternatively offer a captain option
  to use `--no-approve` and accept no Armada MCP.
- Risk: writing `.pi/mcp.json` into a dock; the exclude entry and cleanup must be robust to
  crashes (orphan recovery should delete it).
- Risk: pi auto-compaction and auto-retry change usage mid-run; telemetry tests must include
  compaction and retry fixtures.
- Risk: a slow stdout reader stalls pi (documented); Armada's reader must keep draining while
  formatting.
- Risk: `pi` command name collisions on PATH.
- Open: should Pi ever use RPC mode (steering, `get_session_stats`, `extension_ui_request`
  approvals)? Revisit after Phase 6.
- Open: `SupportsResume` could become true with `--session-id <missionId>`; out of scope until
  Armada needs resume for non-Claude runtimes.

## 7. Size estimates

| Phase | Scope | Rough size |
|---|---|---|
| 1 | Enum, parser, builder, runtime, factory, detection, switches + tests | M: ~10 files changed, 2 new, 1 day |
| 2 | Protocol models, final message, errors, telemetry, log, server consumers, mission stream + fixture tests | L: ~15 files, 8 new, 2-3 days |
| 3 | MCP binding, worktree exclude/cleanup, permission mapping + tests | M: ~6 files, 1-2 days |
| 4 | Helm, TUI, MCP schema text, dashboard, i18n (8 locales), docs, Postman, API surface | M: ~35 files mostly text, 1-2 days |
| 5 | Smoke suite and stubs (spread across phases 1-3) | S: 0.5-1 day |
| 6 | Optional permission-gate extension | M: 1-2 days, plus UNVERIFIED pi behavior |
