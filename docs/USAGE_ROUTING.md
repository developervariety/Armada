# Smart Routing (usage-aware routing)

> **Naming.** This capability is now called **Smart Routing** (formerly "Routing V2").
> The model-tier selector it sits over is now called **Legacy Routing**. Settings keys,
> event reasons, and the `modelTier.usageRouting` block keep their identifiers.

Armada can keep the preferred account for each persona, then use an approved
fallback when that account runs low. Enable `modelTier.usageRouting` in Settings.
The default is disabled, with no accounts, prices, or persona routes.

## Routing rules

`personaRoutes` lists accounts in preference order. Each route can restrict
`models`; an empty model list accepts any eligible model on that account.
These lists are strict: an unlisted account cannot receive that persona's work.
Captain persona restrictions and the required model tier still apply. A route
cannot create a captain or make an unsupported model available. Existing retry
exclusions still apply when another approved captain is available.

| State | Routine work | Reserved persona or priority |
| --- | --- | --- |
| Normal | Use the first eligible route | Use the first eligible route |
| Low | Prefer an approved Normal fallback; use Low if none exists | Keep the preferred route |
| Reserve | Wait or use an approved fallback | Keep the preferred route |
| Exhausted | Wait or use an approved fallback | Wait or use an approved fallback |
| Unknown | Apply `unknownUsagePolicy` | Apply `unknownUsagePolicy` |

Armada does not sort accounts by remaining allowance. A preferred account at
45% stays preferred over one at 95%. A routine mission can move away when the
preferred account reaches its Low threshold. When V2 is enabled, it replaces the old preference system. Within-tier ranking,
non-native-first preference, capability preference scoring, persona default
captain resolution, and the old global high-tier slot reserve do not select or
reorder V2 candidates. Account reserves replace that global reserve. Model tier
classification, persona restrictions, and explicit mission/stage model
requirements remain constraints. Already-running missions are not moved.

A persona without a route waits with `v2_persona_route_not_configured`. Add a
`"*"` route list for a shared default, or configure every persona. Unmapped
captains cannot become implicit fallbacks. Within a route, models follow the
configured list order; equal candidates use stable captain ID order. Disable
V2 to restore the legacy policy; its stored settings remain available. Existing
queued missions keep their persisted model requirements, so inspect them during
migration if the old policy wrote a concrete model pin.

### Shape tags and the D16 routing hint

A route can carry an optional `shapes` tag list, for example `["mechanical",
"doc-only"]`, `["reasoning-heavy", "port-fidelity"]`, or `["policy-tolerant"]`.
A route with no tags is eligible for every shape, so a configuration that sets
none behaves exactly as before. Tags never widen or narrow eligibility; they
only order routes that are already eligible.

When the `routing_hint` typed decision (D16) is enabled and V2 is on, the model
reads the work and chooses a shape. Among the routes already approved and found
eligible for a routine mission in the Normal state, the first route whose
`shapes` contains the chosen shape is preferred over the plain list order. When
the work is policy-sensitive (`policy_sensitive >= 0.9` — authorized seed-key,
SecurityAccess, or similar diagnostic content a safety-tuned runtime has refused
before), a route tagged `policy-tolerant` is preferred; when none is configured,
the plain V2 default applies and the decision records `no_tolerant_route`.

The hint only reorders eligible routes. It never creates a route, never picks an
unlisted account or model, never moves a running mission, and never overrides
Reserve or Exhausted handling. Reserved personas and reserved-priority missions
are never reordered. Every state rule in the table above still applies after the
reorder. The tags are set by the operator from the shadow success table, never
by the model. Disabling V2 or the decision restores the plain list order.

The defaults are Low at 25% remaining, Reserve at 10%, and recovery at 35%.
After entering Low, an account stays there until recovery. All applicable
windows bind: the most restrictive window wins. `windowModels` maps an exact
reported window name to captain model IDs. Unmapped windows apply to all
models. The Dashboard account summary shows the worst account state; dispatch
evaluates only windows that apply to the selected model.

`reservedPersonas` and `reservedPriorityAtOrAbove` define important work.
Lower numeric mission priorities are more important. The priority rule is off
unless set. `resetGraceMinutes` can release Low near a reported reset; it never
releases Reserve or Exhausted. A passed reset makes the old observation Unknown
until a new measurement arrives. Armada never assumes that a reset means 100%.

`maxConcurrentMissions` limits the account's active mission captains and
in-flight mission reservations, across all listed captain IDs. Zero disables
this limit. It does not control work launched outside Armada. When usage blocks
assignment, the mission stays queued as `WaitingForProviderUsage`; the scheduler
retries without treating this wait as an assignment failure.

## Collection and credentials

| Collector | Read source | Credential reference |
| --- | --- | --- |
| `Codex` | `codex app-server` JSON-RPC `account/rateLimits/read` | The account's `homeDirectory` as `CODEX_HOME`, or the Admiral service user's login when no home is set |
| `Claude` | `https://api.anthropic.com/api/oauth/usage` | `credentialEnv` names an OAuth token variable, or `credentialFilePath` points to Claude's credentials JSON |
| `Cursor` | `https://cursor.com/api/usage-summary` | Environment variable or file containing the Cookie header value |
| `OpenCodeGo` | `https://opencode.ai/zen/go/v1/usage` | Environment variable or file containing an API key; an OpenCode auth JSON file with an `opencode-go` API entry also works |
| `File` | Bounded local normalized JSON snapshot | `usageFilePath` |
| `Manual` | `manualSnapshot` in the policy | None |

The HTTP adapters follow the public implementation in
[CodexBar](https://github.com/steipete/CodexBar). See its
[Claude](https://github.com/steipete/CodexBar/blob/main/docs/claude.md),
[Cursor](https://github.com/steipete/CodexBar/blob/main/docs/cursor.md), and
[OpenCode](https://github.com/steipete/CodexBar/blob/main/docs/opencode.md)
provider notes. CodexBar is MIT licensed; see [the notice](licenses/CodexBar.txt).
These are provider account endpoints, not a stable common billing API. A
provider schema or authentication change can make usage Unknown.

Collectors issue read requests, not model inference. Each native request has
a 15-second timeout, with at most four collectors running at once. A policy
can contain up to 32 accounts. HTTP redirects are disabled. Files and HTTP bodies are
limited to 64 KiB. Polling uses `refreshIntervalMinutes` (default 5, range 1–60).
An HTTP 429 honors Retry-After, or uses a 15-minute retry delay. It means the
usage endpoint is rate limited; it does not prove model allowance exhaustion.

Use one account per actual shared allowance, with every associated captain ID.
An account without `homeDirectory` shares the server-user login; do not represent
that one login as independent accounts. A Codex account with its own
`homeDirectory` is measured through that home, so two Codex accounts report
separate windows. Claude reads `<homeDirectory>/.credentials.json` and OpenCodeGo
reads `<homeDirectory>/opencode/auth.json` when no reference is supplied. Without
a home, Claude defaults to `~/.claude/.credentials.json`. OAuth needs access to account usage. Armada does not
refresh or modify login files. Cursor needs an explicit session cookie reference;
Armada does not import browser cookies. OpenCodeGo measures the Go subscription,
not every model or third-party provider that the OpenCode runtime can run.
Use separate accounts and File or Manual data for other services.

Store secrets outside Armada settings. `credentialEnv` is a variable **name**,
not a token. Protect credential and usage files for the Admiral service user.
API results include normalized usage and safe error codes, not raw responses or
authentication headers. Credentials are read only on the server.

Codex primary/secondary windows retain their bucket names. Claude keeps its
session, weekly, and model-specific windows separate. Cursor keeps its Cursor
model and third-party pools separate when available; their average is not an
additional limit. Go keeps rolling, weekly, and monthly windows separate.
Configure `windowModels` after inspecting reported names and confirming which
captain models consume each pool. Unknown fields remain Unknown; local token
counts are not treated as subscription allowance.

## Account logins

By default every subscription captain of one runtime uses the one login of the
Admiral service user. An account can own a separate login instead. There is no
limit per runtime beyond the policy's 32 accounts. This is disabled by default:
an account with no `runtime`, or no `homeDirectory`, `launchCredentialEnv`, or
`launchCredentialFile`, launches its captains exactly as before.

**Owner decision required before rollout.** Confirm that each additional
subscription account is permitted for this use under the provider's terms
(Anthropic, OpenAI, Cursor, OpenCode) before you add a second-account captain.
Armada does not create accounts, sign in, copy login files, refresh tokens, or
accept billing terms.

| `runtime` | Launch switch | Login check |
| --- | --- | --- |
| `ClaudeCode` | `CLAUDE_CONFIG_DIR=<homeDirectory>` | `<homeDirectory>/.credentials.json` exists |
| `Codex` | `CODEX_HOME=<homeDirectory>` | `<homeDirectory>/auth.json` exists |
| `OpenCode` | `XDG_DATA_HOME=<homeDirectory>` | `<homeDirectory>/opencode/auth.json` exists |
| `Cursor` | `CURSOR_API_KEY` from the variable named by `launchCredentialEnv`, or read from `launchCredentialFile` | That variable is set, or that file exists and is not empty |

- `homeDirectory` is an absolute path, and it holds only a path. Keep one home
  per login, for example under the Admiral's `accounts/<id>/` folder, mode
  0700. Log in inside the home (`CODEX_HOME=<home> codex login --device-auth`).
  Do not copy `auth.json` or `.credentials.json` between homes; token rotation
  invalidates copies.
- Cursor keeps its normal `HOME`, so git, gh, and ssh configuration stay
  visible. `launchCredentialEnv` is a variable **name**; Armada reads the value
  at launch and never stores it. `launchCredentialFile` is the alternative: the
  absolute path of `cursor-api-key` inside the account's own folder
  (`<data directory>/accounts/<id>/cursor-api-key`). Settings saves reject any
  other path, and an account cannot set both. The Cursor usage collector still needs its own
  session-cookie reference in `credentialEnv` or `credentialFilePath`.
  `XDG_DATA_HOME` also moves OpenCode session storage into the home.
- Codex external-provider profiles for a captain on an account are written into
  that account's `CODEX_HOME`.
- Validation rejects an account whose `runtime` is not one of the four above,
  whose collector measures a different runtime, or whose listed captain uses a
  different runtime. It also rejects a login on an account that lists a captain
  carrying its own `apiKey` or `apiBaseUrl`; such captains keep their launch.
- A missing home, missing login file, or unset Cursor key variable makes the
  account `Exhausted` with reason `account_home_missing`,
  `account_login_missing`, or `account_launch_credential_unavailable`. This is
  visible in settings status and the usage preview, and blocks assignment when
  routing is enabled. When no approved route is left, the routing decision
  `reason` is the first such account code rather than
  `usage_reserve_exhaustion_or_account_capacity`; the preview returns it, and
  the scheduler logs it for the mission that waits as
  `WaitingForProviderUsage`. A launch that still reaches such an account fails
  with the same reason; it never falls back to the shared login.

### Logging in from the Dashboard

The Routing tab's **Subscription accounts** section does the whole setup without
a shell. The JSON policy editor stays available under **Advanced**.

**Add account.** Pick the runtime and type a name. The account ID is the name
in lower case with other characters replaced by hyphens, made unique among
existing accounts. The server creates `<data directory>/accounts/<id>` with
mode 0700, and the account is saved with its `runtime`, a matching usage
collector (`Codex`, `Claude`, `OpenCodeGo`; `Manual` for Cursor), and
`homeDirectory` set to that folder, or `launchCredentialFile` set to its
`cursor-api-key` for Cursor.

**Log in**, by runtime:

| Runtime | Dashboard step | What the server runs or writes |
| --- | --- | --- |
| Codex | Start device login, open the link, enter the code | `codex login --device-auth` with `CODEX_HOME=<folder>` |
| Claude Code | Start sign-in, open the link, paste the code shown after sign-in | `claude auth login` with `CLAUDE_CONFIG_DIR=<folder>`; the pasted code goes to its standard input |
| OpenCode | Enter the OpenCode Go API key | Merges `{"opencode-go": {"type": "api", "key": ...}}` into `<folder>/opencode/auth.json` (mode 0600), keeping other entries; an unreadable file is left unchanged |
| Cursor | Enter the Cursor API key | Writes `<folder>/cursor-api-key` (mode 0600) |

Cursor also offers a browser login (`cursor-agent login` with
`NO_OPEN_BROWSER=1` and `HOME` set to the account folder, so the Admiral
user's own Cursor login is untouched). Captains still launch with the API
key, so the key step is the one that makes the account usable.

**Assign captains.** Tick captains of the same runtime; a captain already on
another account, or one with its own provider key or base URL, cannot be
ticked. **Clone** creates a new captain with the same runtime, model, and
personas, named `<captain>-<account>`, and assigns it.

The page checks a pending login every three seconds. A login succeeds when the
CLI exits cleanly; Armada then discards the cached login probe and starts a new
one. Only the verification URL (on the provider's own domain) and the device
code are read from the CLI output; the rest is discarded and never logged.
A key or pasted code is sent once, cleared from the page, and never returned,
logged, stored in settings or events, or recorded in request history. One login
runs per account. A pending login is stopped after 15 minutes
(`account_login_expired_before_completion`), when cancelled, and when the
Admiral stops. A CLI that prints no link within 30 seconds is stopped with
`account_login_prompt_not_found`. The REST routes are listed in
[the REST API reference](REST_API.md#subscription-account-logins).

### Login status probe

The file check is a fast pre-filter. When it passes, Armada also runs the
runtime's own status command in the account home to catch an expired or revoked
login:

| `runtime` | Status command | Result |
| --- | --- | --- |
| `ClaudeCode` | `claude auth status --json` with `CLAUDE_CONFIG_DIR` | `loggedIn: true` is ready; `loggedIn: false` is `account_login_expired` |
| `Codex` | `codex login status` with `CODEX_HOME` | Exit 0 is ready; `Not logged in` is `account_login_expired` |
| `OpenCode` | None used | `opencode auth list` exits 0 with human-readable text even with no credential, so only the file check applies |
| `Cursor` | None used | `cursor-agent status` reports the stored login and ignores `CURSOR_API_KEY`, so only the variable or key file check applies |

A probe that runs longer than `loginProbeTimeoutSeconds` (default 10, range
1–60) is stopped and reports `account_login_probe_timeout`. A CLI that cannot
start reports `account_login_probe_unavailable`, and an output the probe cannot
read reports `account_login_probe_failed`. Each of these makes the account
`Exhausted` with that reason. A result is reused for
`loginProbeIntervalMinutes` (default 10, range 1–1440); settings status shows
`loginCheckedUtc`.

Probes run in the background. Dispatch, status, and preview read the last
result and never wait for a probe, so a hanging CLI cannot stall the scheduler.
Until an account's first probe finishes, only the file check applies. Command
output is read only to decide the result; it is never logged or returned. The
probe cannot detect a revoked OpenCode credential or an invalid Cursor key; the
first launch that fails on authentication then holds the account, as described
below.

When a captain fails on a quota, billing, or authentication signal, Armada holds
its **whole account** Exhausted until the provider's retry time (reason
`account_provider_failure`, with `exhaustedUntilUtc`). Idle captains on the same
account are quarantined until then, so the re-routed mission cannot land on
them. A busy captain on the account keeps its running mission, and routing gives
it no new work while the hold lasts. The hold is kept in memory and ends at a
restart. An operator override replaces it.

## Unknown data and overrides

`maxAgeMinutes` defaults to 15. Collection failures retain the last valid
measurement with its original observation time and a safe error code. Stale,
missing, invalid, or reset data uses `unknownUsagePolicy`: `Allow` retains normal
routing, `Conserve` treats the account as Low, and `Block` prevents assignment.
The default is Allow for compatibility. Choose Block when verified allowance is
required. A known exhausted window always blocks, even if another window is
unknown. Manual snapshots also expire; they are not permanent allowances.

An optional `overrideState` (`Normal`, `Low`, `Reserve`, or `Exhausted`) requires
`overrideUntilUtc`. The override is visible as `operator_override`. Use a short
expiry for an operator decision. It overrides measured usage, including
exhaustion. Measurements and recovery state are held in memory; they are
refetched after restart. Unchanged account sources retain their cache and
recovery state across settings updates.

## Dashboard and API

The Settings hub has an admin **Routing** tab. Its Smart Routing part has the
guided **Subscription accounts** section (see
[Logging in from the Dashboard](#logging-in-from-the-dashboard)), an enable
control, budget fields, reported usage, a draft preview, and an **Advanced**
section with the account template and the full editable policy JSON. A guided
account change saves only that change to the saved policy; unsaved JSON edits
are kept as a draft and are not sent with it. Its save sends only
`modelTier.usageRouting`, so it never replaces the model routing policy edited
in the other part of the tab, and a refresh keeps unsaved edits. The policy
hot-reloads; no restart is required.
`monthlyBudget`, `currency`, and account `monthlyCost` are operator-entered
planning values. They show a total and an over-budget indicator. They do not
purchase plans, enforce a billing cap, or measure prepaid spending.

`GET /api/v1/settings` returns `providerUsage` alongside `modelTier`.
`PUT /api/v1/settings` accepts `modelTier.usageRouting` as a full policy
replacement. Invalid policies return 400 before application.

`POST /api/v1/settings/usage-preview` accepts `persona`, `priority`, optional
`preferredModel`, and an optional draft `usageRouting`. It requires settings
write permission. It returns ordered eligible candidates, usage states, a
reason, warnings, and scope. A draft preview does not save settings or assign a
mission. It can fetch usage. Preview shares the policy evaluator with dispatch,
but does not reserve captains or run every provisioning and voyage gate; it is
not a promise of the final assignment.

Start with [the generic example](../factory/usage-routing.example.json), replace
captain IDs and account sources, and keep the saved policy disabled. Enable
only the unsaved draft to preview each persona before saving an enabled policy. Set window mappings and unknown-data behavior before enabling. Keep
personal subscription costs and account configuration in your local settings.

A File snapshot has this form (supply current UTC timestamps):

```json
{
  "observedUtc": "2026-01-01T12:00:00Z",
  "source": "operator-export",
  "windows": [
    { "name": "weekly", "remainingPercent": 40,
      "resetsUtc": "2026-01-05T12:00:00Z", "models": [] }
  ]
}
```

Write exports atomically. The observation time must reflect the measurement,
not the time the file was copied. Missing percentages must be null, not zero.
