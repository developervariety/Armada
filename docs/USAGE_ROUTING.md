# Smart Routing

Armada has two routing modes. Both use the same settings keys.

- **Legacy Routing** assigns work when `modelTier.usageRouting.enabled` is
  false. It uses model tiers, persona locks (`AllowedPersonas`), the
  within-tier preference order, non-native-first, capability scoring, the
  persona default captain, requested captains, and the high-tier slot
  reserve.
- **Smart Routing** assigns work when `modelTier.usageRouting.enabled` is
  true. It is Legacy Routing plus four additions: the usage filter, the
  per-persona model lists, the `capacity_escalation` typed decision, and
  optional persona route restrictions.

The default is disabled, with no accounts, prices, model lists, or routes.

## How Smart Routing selects a captain

Smart Routing never re-ranks captains. It starts from the Legacy Routing
order and only removes, moves, and groups captains. The steps are:

1. **Route restriction (optional).** When the persona has `personaRoutes`
   (or a `"*"` entry applies), only captains on the named accounts stay in
   the pool. A route with a `models` list also limits the models. A persona
   without routes is not restricted. Route order has no effect.
2. **Legacy Routing order.** The Legacy Routing selector picks its first
   captain, then its next captain from those left, until it picks none. A
   captain that a model or persona constraint excludes is not in the order.
3. **Usage filter.** Each captain gets a verdict from its account state:

   | Account state | Verdict |
   | --- | --- |
   | Exhausted (measured windows, login missing or expired, provider-failure hold) | Removed |
   | Account at `maxConcurrentMissions` | Removed (`account_concurrency_limit`) |
   | Low or Reserve | Demoted: moved after every kept captain |
   | Normal | Kept in its position |
   | Unknown | `unknownUsagePolicy`: `Allow` keeps, `Conserve` demotes, `Block` removes |
   | No account | Kept in its position (`no_usage_account`) |

   Demoted captains keep their Legacy Routing order among themselves.
   `reservedPersonas` and `reservedPriorityAtOrAbove` keep a Low or Reserve
   captain in its position for that work (`reserved_work_keeps_position`).
   A retry does not return to a captain on its retry skip list while any
   other kept or demoted captain remains.
4. **Persona model groups (optional).** See the next section.
5. The first captain that remains is assigned.

When the Legacy Routing order has captains but the usage filter removes all
of them, the mission waits as `WaitingForProviderUsage`. The scheduler
retries it without counting an assignment failure. The reason is the first
account code (for example `account_login_expired`), or
`usage_exhausted_or_account_capacity`.

Armada never sorts by remaining allowance. A kept account at 45% stays
ahead of a kept account at 95% when Legacy Routing puts it first.
Already-running missions are never moved.

### Persona model lists

`personaModels` gives a persona three model lists:

```json
"personaModels": {
  "Worker": {
    "default": ["composer-2.5", "opencode-go/deepseek-v4-flash"],
    "lighter": ["gpt-5.6-luna"],
    "stronger": ["cursor-grok-4.6-high"]
  }
}
```

Persona names match after normalization (`TestEngineer` and
`Test Engineer` are the same persona). `default` must not be empty.

For a persona with an entry, Smart Routing builds groups from the
usage-filtered order. The chosen list goes first. The other lists follow in
the order default, stronger, lighter. A captain goes into the first group
whose list contains its model. Captains whose model is in no list go last,
so a persona is never starved by its lists. Inside each group, the
Legacy Routing and usage order stays. The first captain of the first
non-empty group is assigned.

The chosen list is `default` unless the `capacity_escalation` decision
chooses another list. A mission with a concrete `preferredModel` skips the
model lists; the pin wins. A tier requirement stays a Legacy Routing
constraint.

### The capacity decision

When a persona entry has a `lighter` or `stronger` list, Armada asks the
`capacity_escalation` typed decision one closed question at assignment. The
state holds the persona, the objective (or mission) title, the head of its
description, and the three model lists, after redaction. The answers are:

| Answer | Meaning |
| --- | --- |
| `lighter` | Routine, well-specified, mechanical work |
| `default` | Work that fits the default model |
| `stronger` | Work harder than the default model is expected to handle: a subtle fix, a cross-repository design, a hard diagnosis |

The decision ships in `Gate` mode at threshold 0.90. Below the threshold,
in `Off` mode, with no key, on a timeout, 429, 529, parse error, or a client
fault, the result is `default`. Every call records the rule verdict and the
model verdict (`typed_decision.gated`, `typed_decision.shadow`, or
`typed_decision.unavailable`). Armada keeps the reading per mission in
memory for 30 minutes (at most 1024 missions), so another assignment
attempt does not ask again. The reading only orders the groups. It never
makes a captain eligible, approves, lands, dispatches, or edits a record.

### Retired settings

The route `shapes` tags and the `routing_hint` typed decision are retired.
Settings files that still contain them load without error; Armada ignores
the values. Routes no longer define an order. After you add
`personaModels`, remove any `personaRoutes` entry that only expressed a
preference. Keep a route only to restrict a persona to named accounts.

### Account thresholds

The defaults are Low at 25% remaining, Reserve at 10%, and recovery at 35%.
After entering Low, an account stays there until recovery. All applicable
windows bind: the most restrictive window wins. `windowModels` maps an exact
reported window name to captain model IDs. Unmapped windows apply to all
models. The Dashboard account summary shows the worst account state; dispatch
evaluates only windows that apply to the captain's model.

Lower numeric mission priorities are more important. The priority rule is off
unless set. `resetGraceMinutes` can release Low near a reported reset; it never
releases Reserve or Exhausted. A passed reset makes the old observation Unknown
until a new measurement arrives. Armada never assumes that a reset means 100%.

`maxConcurrentMissions` limits the account's active mission captains and
in-flight mission reservations, across all listed captain IDs. Zero disables
this limit. It does not control work launched outside Armada.

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
  visible in settings status and the usage preview, and removes the account's
  captains when Smart Routing is enabled. When no captain is left, the routing
  decision `reason` is the first such account code rather than
  `usage_exhausted_or_account_capacity`; the preview returns it, and the
  scheduler logs it for the mission that waits as `WaitingForProviderUsage`. A launch that still reaches such an account fails
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

**Refresh usage.** Each account card shows its usage state and when it was
last observed. Usage is otherwise read at most once per
`refreshIntervalMinutes` (default 5), and only when something reads it (the
settings page, a usage preview, or routing); data older than the account's
`maxAgeMinutes` (default 15) counts as Unknown. The section states both values.
**Refresh usage** reads that one account now, bypassing the interval, and
reruns its login check. It still honours a provider retry-after: while one is
active the provider is not called and the card says when the next read is
allowed (`usage_refresh_rate_limited`).

**Delete account.** The Manage view's **Delete account** asks for confirmation
naming the account, then removes it from the policy and from every persona
route, and removes its login files under `<data directory>/accounts/<id>` on
the server. A `homeDirectory` elsewhere is left in place. The button is
disabled while captains are assigned: unassign them first, so no captain
silently falls back to the shared login. An unsaved edit in the Advanced JSON
editor that still lists the account keeps it until that edit is discarded.

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
`preferredModel`, optional `missionTitle` and `missionText`, and an optional
draft `usageRouting`. It requires settings write permission. It returns the
steps of the selection:

| Field | Content |
| --- | --- |
| `legacyOrder` | The Legacy Routing order of the idle, route-restricted captains |
| `usageFilter` | One verdict per captain: `captainId`, `model`, `accountId`, `state`, `outcome` (`kept`, `demoted`, `removed`, `outside_routes`), `reason` |
| `modelGroups` | The persona model groups in the order tried, each with its captain IDs |
| `capacity` | `choice`, `source`, and `asked` |
| `candidates` | The final order |
| `chosen` | The captain that would be assigned, or null |
| `reason` | The selection or wait reason |

Without `missionTitle` and `missionText`, the preview does not call the
typed-decision client and reports the `default` list (source
`no_work_text`). With text, it asks the `capacity_escalation` decision once
and does not cache the reading. A draft preview does not save settings or
assign a mission. It can fetch usage. Preview shares the selector with
dispatch, but it does not reserve captains, apply requested captains or retry
exclusions, or run every provisioning and voyage gate; it is not a promise of
the final assignment.

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
