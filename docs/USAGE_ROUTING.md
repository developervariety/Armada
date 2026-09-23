# Smart Routing

Armada selects a captain for a mission in three layers. Each layer works only
on the captains the layer before it kept.

| Layer | Name | Decides | Inputs |
| --- | --- | --- | --- |
| 1 | Eligibility | Which captains may take the mission | Persona locks (`AllowedPersonas`) and the tier floor |
| 2 | Order (Legacy Routing) | The order of the eligible captains | Tier, preference rank, capability hint, non-native-first, preferred persona |
| 3 | Choice (Smart Routing) | Which admitted captain goes first | Persona routes, usage filter, persona model lists, `capacity_escalation` |

Layer 3 runs only when `modelTier.usageRouting.enabled` is true. Without it,
the Legacy Routing order stands. Layer 3 never adds a captain that layer 1
excluded, and it never changes the tier floor.

## Where the routing facts live

| Fact | Where | Edited in |
| --- | --- | --- |
| Capability tier (`Economy`, `Standard`, `Premium`) | Captain record `tier`; null classifies it from the model name | Captain modal and captain detail page |
| Preference rank (integer, -1000 to 1000, default 0) | Captain record `preferenceRank` | Captain modal and captain detail page |
| Minimum tier | Persona record `minimumTier` | Persona detail page |
| Reserved Premium slots | Setting `modelTier.reservedHighTierSlots` | Settings > Routing |
| Non-native-first | Setting `modelTier.preferNonNativeFirst` | Settings > Routing |
| Capability profiles | Settings `modelTier.modelCapabilityProfiles` and `capabilityHintDimensionMap` | `settings.json` |
| Smart Routing policy | Setting `modelTier.usageRouting` | Settings > Routing |

Armada reads captain and persona records again on every dispatch pass, and the
usage preview reads them for each preview. A record edit applies within one
pass. No restart is required.

## Layer 1: eligibility

A captain is eligible when both of these are true:

- Its `AllowedPersonas` allows the mission's persona (null allows any persona).
- Its tier is at or above the mission's tier floor.

The tier floor comes from the mission:

| Mission | Tier floor |
| --- | --- |
| `preferredModel` `low` | Economy |
| `preferredModel` `mid` (or `quick`, `medium`) | Standard |
| `preferredModel` `high` | Premium |
| Persona `minimumTier` | Configured tier, combined with any higher mission floor |
| Concrete model pin that an idle captain runs | None. Only captains that run the pinned model are eligible |
| Concrete model pin that no idle captain runs | The tier of the captains that run that model; else the model family tier; else none |
| No `preferredModel` | None |

A persona minimum tier is a hard floor. Armada tries the lowest tier at or
above both the persona minimum and the mission request. A Standard minimum can
use a Premium captain when no Standard captain is available. A captain below
the effective floor is never chosen, even when a persona model list names its
model.

## Layer 2: order (Legacy Routing)

The eligible captains are ordered by these keys, first key first:

1. **Tier.** The lowest tier at or above the floor goes first, so a Premium
   captain is not used while a Standard captain can take `mid` work. Without
   a floor the order is Standard, then Premium, then Economy.
2. **Capability hint.** When the mission has a `capabilityHint` that maps to a
   profile dimension, the model with the higher score for that dimension goes
   first.
3. **Preference rank.** A higher `preferenceRank` goes first.
4. **Non-native-first.** When `preferNonNativeFirst` is true, a captain with
   its own `apiBaseUrl` on a non-OpenCode runtime goes before a native captain.
5. **Preferred persona.** A captain whose `PreferredPersona` is the mission's
   persona goes first.
6. **Random tie.** Among captains equal on every key, Armada picks a model at
   random, so a model with more captains is not favoured.

A retry avoids the captains on its retry skip list while another eligible
captain remains. A requested captain and a fallback tier are applied before
this layer; see [Requested captain and fallback tier](#requested-captain-and-fallback-tier).
`reservedHighTierSlots` holds idle Premium captains for downstream work when
the pipeline can produce a later review stage.

## Retired tier settings and the one-time migration

`modelTier.midTierModels`, `highTierModels`, `familyClassificationRules`,
`specialistPersonas`, `withinTierStrategy`, and `withinTierPreferenceOrder` are
retired. At startup, after personas are seeded, Armada moves them onto
records once:

| Retired key | Becomes |
| --- | --- |
| `highTierModels`, a `high` family rule | Captain tier Premium |
| `midTierModels`, a `mid` or `low` family rule | Captain tier Standard |
| A model none of the lists or rules classified | Captain tier Economy (only when a list or rule was set) |
| `withinTierPreferenceOrder` (with `withinTierStrategy` `PreferenceOrderThenRandom`) | Captain `preferenceRank`: in a list of n models the first model ranks n, the last ranks 1, unlisted models rank 0 |
| `specialistPersonas` | `minimumTier: Premium` on matching persona records, except Test Engineer, which gets Standard |

The migration pins a tier only when the captain's effective tier differs from
the mapped tier. It logs each captain and persona it changes, and warns for a
specialist name that matches no persona record. It then copies the settings
file to `settings.json.pre-tier-migration-<UTC time>.json`, removes the
retired keys, and saves `modelTier.tierRecordsMigratedUtc`. A settings file
with that stamp is never migrated again: retired keys in it load and are
ignored, and the startup log warns that they are present. Applying the
migration twice changes no record. The settings API ignores retired keys.

The mapping keeps each captain's selection. Review the log after the upgrade.
To change the order afterwards, edit the captain ranks. For example, a Judge
order of `a`, `b`, `c` migrates to ranks 3, 2, 1; set `c` to 4 to try it first.

## Layer 3: choice (Smart Routing)

Smart Routing never re-ranks captains and never admits one. It starts from
the Legacy Routing order and only removes, moves, and groups captains. The
steps are:

1. **Route restriction (optional).** When the persona has `personaRoutes`
   (or a `"*"` entry applies), only captains on the named accounts stay in
   the pool. A route with a `models` list also limits the models. A persona
   without routes is not restricted. Route order has no effect.
2. **Legacy Routing order.** The Legacy Routing selector picks its first
   captain, then its next captain from those left, until it picks none. A
   captain that layer 1 (persona lock or tier floor) excludes is not in the
   order, and no later step adds it back.
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

A list entry is **dead** when no captain who may serve that persona (persona
lock plus the capability tier floor) runs that model. Saving the policy still
succeeds and reports every dead entry in `personaModelHealth` on
`GET`/`PUT /api/v1/settings` and on `usage-preview`. Startup logs one summary
so existing misconfiguration is visible without anyone asking. When the
capacity-chosen list matches nothing eligible, assignment falls through to
the remaining groups and the reason is `persona_models_matched_nothing`; an
event `routing.persona_model_list_dead` names the list. A list that did
supply the captain is `persona_models_applied_<group>:<captainId>`. Dead
lists never refuse dispatch.

#### Persona minimum tier

`minimumTier` is an optional `Economy`, `Standard`, or `Premium` floor on a
persona. Null means that the mission request and normal Legacy Routing order
control. Armada combines this floor with `preferredModel` by taking the higher
one. A concrete model pin remains a model restriction and must also meet the
persona floor. Persona locks, tenant ownership, quarantine, and assignment
reservations remain separate hard gates. Model lists and the Jev capacity
decision only order eligible captains; neither can lower the floor.

Existing records with the legacy specialist flag receive an explicit floor at
schema migration. Test Engineer receives Standard; other flagged personas
receive Premium to preserve their configured minimum. New built-in personas
use explicit defaults: Test Engineer is Standard, while Judge, Architect,
Product Manager, and Usability Engineer are Premium. Operators can change or
clear a persona floor in its detail page. Routing preview and model-list
health use the same effective floor as assignment.

The chosen list is `default` unless the `capacity_escalation` decision
chooses another list. A mission with a concrete `preferredModel` skips the
model lists; the pin wins. The tier floor stays a layer 1 constraint: a model
list only orders the captains layer 1 admitted, so a `lighter` model below the
floor is never chosen, and the capacity reading never raises or lowers the
floor.

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
the values. Routes restrict a persona to named accounts; they do not set the
order. After you add
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
| `Cursor` | Cursor API key exchange and `GetCurrentPeriodUsage`; legacy `https://cursor.com/api/usage-summary` is also supported | `launchCredentialEnv` or `launchCredentialFile` for the API key; `credentialEnv` or `credentialFilePath` for a legacy Cookie header |
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
refresh or modify login files. Cursor exchanges the account's saved API key for
a short-lived usage token and reads the current billing period. Existing cookie
references continue to use the legacy usage-summary endpoint; Armada does not
import browser cookies. OpenCodeGo measures the Go subscription, not every model
or third-party provider that the OpenCode runtime can run.
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

**Require account login.** `requireAccountLogin` (default `false`) refuses Claude Code, Codex,
OpenCode, and Cursor captains that have no account login binding. The refusal applies to
mission launch, Ask, planning sessions, and backlog refinement. Captains with their own
`apiKey` or `apiBaseUrl`, and other runtimes, are unchanged. The Routing tab lists who would
be refused before you turn the setting on. Usage-preview returns the same list as
`accountLoginRefusals`.

### Retire shared login files

Enable `requireAccountLogin` only after the draft usage preview has no
`accountLoginRefusals`. Check the saved policy and run an account-bound Ask
request. Verify the launch paths for planning and refinement too. A preview
checks login-file presence; it does not prove that a provider accepts a token.
Keep the execution hold and scheduler pause in place during this check.

Before retirement, check for host-side operator sessions that still use the
shared login. Confirm that account login paths are separate files, not links to
the shared path. Retire only the service user's runtime credential files:
`~/.codex/auth.json`, `~/.claude/.credentials.json`,
`~/.local/share/opencode/auth.json`, and `~/.config/cursor/auth.json` when present.
Use a protected rollback location outside the runtime search paths during the
change. Do not copy a token into an account home or print its contents.

Keep each mounted folder, its configuration, profiles, and session history.
Keep the account folders and their login files. Recheck the refusal preview and
an account-bound Ask request after retirement. Restore only the affected login
file if a dependent operator session was missed; do not disable the gate to
hide an account launch failure. Remove the rollback copy after verification.

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
  other path, and an account cannot set both. The Cursor usage collector reads
  the API key through `launchCredentialEnv` or `launchCredentialFile`. Legacy
  session-cookie references in `credentialEnv` or `credentialFilePath` remain
  supported.
  `XDG_DATA_HOME` also moves OpenCode session storage into the home.
- Codex external-provider profiles for a captain on an account are written into
  that account's `CODEX_HOME`.
- Validation rejects an account whose `runtime` is not one of the four above,
  whose collector measures a different runtime, or whose listed captain uses a
  different runtime. It also rejects a login on an account that lists a captain
  carrying its own `apiKey` or `apiBaseUrl`; such captains keep their launch.
  The same validation, including the check that a Cursor key file sits in the
  account's own folder under the Admiral's account root, applies to
  `PUT /api/v1/settings`, `POST /api/v1/settings/reload`, and edits picked up by
  the settings-file watcher; a refused reload keeps the current policy.
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
collector (`Codex`, `Claude`, `OpenCodeGo`, `Cursor`), and
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
user's own Cursor login is untouched). A browser login never launches a
captain: replacing `HOME` would hide git, gh, and ssh configuration, so
captains use only the API key file or named variable. A missing key is
`account_launch_credential_unavailable`; save the key on the account card.
The dashboard creates Cursor accounts with collector `Cursor`. Set existing
Cursor accounts with collector `Manual` to `Cursor` to measure usage from the
saved key. If an account remains `Manual`, usage is Unknown and routing follows
`unknownUsagePolicy` (default `Allow`).

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

The Settings hub has an admin **Routing** tab. Its Legacy Routing part edits
the reserved Premium slots and non-native-first; captain tiers and ranks are
edited on the captains, and persona minimum tiers on the persona detail page. Its Smart
Routing part has:

- a **Routing mode** switch between **Legacy Routing** and **Smart Routing**
  (it sets `modelTier.usageRouting.enabled` in the draft). Legacy Routing needs
  no Jev key. Smart Routing still applies account usage without Jev and tries
  Default first;
- the guided **Subscription accounts** section (see
  [Logging in from the Dashboard](#logging-in-from-the-dashboard));
- budget fields;
- **Persona model lists**: one row per persona from the personas catalogue,
  plus any persona already in `personaModels`, with Default, Lighter, and
  Stronger model chips. Model options are the captains' models. Each model shows how many captains run it, and a warning chip
  when every one of those captains is on an Exhausted account. A row without
  models has no entry, so that persona keeps the Legacy Routing order. **Add
  persona** adds a row for a persona the catalogue does not list;
- **Persona restrictions** (collapsed): the `personaRoutes` entries as
  restrictions, with add and remove per persona and per account, and a note
  when a restriction admits no captain;
- an **Advanced** section with the account template and the full editable
  policy JSON. The table, the restrictions, and the JSON edit the same draft,
  so each view shows the others' changes;
- reported account usage;
- **Preview Smart Routing**: a persona picker, priority, preferred model, and
  optional mission title and text. It shows the chosen captain, the Legacy
  Routing order, a verdict per captain with the layer that decided it
  (eligibility, routes, or usage), its outcome, and its reason,
  the capacity reading (the list chosen first and its source, or "not asked"
  without title and text), and the model groups in the order tried.

A guided account change saves only that change to the saved policy; unsaved
JSON edits are kept as a draft and are not sent with it. **Save routing
policy** sends only `modelTier.usageRouting`, so it never replaces the model
routing policy edited in the other part of the tab, and a refresh keeps
unsaved edits. The policy hot-reloads; no restart is required.
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
| `usageFilter` | One verdict per captain: `captainId`, `model`, `accountId`, `state`, `layer` (`eligibility`, `routes`, `usage`), `outcome` (`kept`, `demoted`, `removed`, `outside_routes`, `excluded`), `reason` (for `eligibility`: `persona_not_allowed`, `below_tier_floor`, or `model_pin_mismatch`) |
| `modelGroups` | The persona model groups in the order tried, each with its captain IDs |
| `capacity` | `choice`, `source`, and `asked` |
| `candidates` | The final order |
| `chosen` | The captain that would be assigned, or null |
| `reason` | The selection or wait reason. `persona_models_applied_<group>:<captainId>` when a list supplied the captain; `persona_models_matched_nothing` when the capacity-chosen list admitted nobody |
| `personaModelHealth` | Per-persona floor, eligible captains, and live/dead marks for each list entry |
| `matchedNothing` / `deadListEntries` | Set when the capacity-chosen list matched no eligible captain |

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

## Model routing and dispatch policy

Product defaults use explicit persona floors; no captain has a pinned tier or
a rank, and the stage-persona title guard is off. A captain's tier and
preference rank are fields on the captain record, and a persona's minimum tier
is a field on the persona record; see
[the three routing layers](#smart-routing). Edit these keys in
`settings.json` or on the Dashboard Settings page:

| Setting | Hot-reload | Product default | Dashboard control |
| --- | --- | --- | --- |
| `modelTier.preferNonNativeFirst` | Yes | `false` | Prefer non-native captains first |
| `modelTier.reservedHighTierSlots` | Yes | `0` | Reserved Premium slots |
| `modelTier.modelCapabilityProfiles` / `capabilityHintDimensionMap` | Yes | empty / built-in hint map | `settings.json` only |
| `modelTier.usageRouting` | Yes | disabled, empty accounts | [Smart Routing: accounts, usage filter, persona model lists, routes, and preview](#smart-routing) |
| `modelTier.tierRecordsMigratedUtc` | Yes | unset | Written by the one-time tier record migration |
| `voyageDispatch.rejectStagePersonaTitlePrefixes` | Yes | `false` | Reject stage-persona title prefixes |
| `voyageDispatch.stagePersonaTitlePrefixes` | Yes | empty | Prefix list |
| `modelProviders` | No (startup) | empty | modelProviders JSON |
| `additionalPromptTemplates` / `additionalPersonas` / `additionalPipelines` | No (startup) | empty | Additional-asset JSON |

| Record field | Default | Dashboard control |
| --- | --- | --- |
| Captain `tier` (`Economy`, `Standard`, `Premium`) | null: classified from the model name | Captain modal and detail page: Capability tier |
| Captain `preferenceRank` (-1000 to 1000) | `0` | Captain modal and detail page: Preference rank |
| Persona `minimumTier` (`Economy`, `Standard`, `Premium`) | unset for custom personas | Persona detail page: Minimum capability tier |

`modelTier.midTierModels`, `highTierModels`, `familyClassificationRules`,
`specialistPersonas`, `withinTierStrategy`, and `withinTierPreferenceOrder` are
retired. At the first startup that finds them, Armada moves them onto captain
tiers, captain ranks, and persona minimum tiers, writes a settings backup, removes the
keys, and stamps `tierRecordsMigratedUtc`. See
[Retired tier settings and the one-time migration](#retired-tier-settings-and-the-one-time-migration).

**Legacy Routing** is layers 1 and 2 while `modelTier.usageRouting.enabled`
is false: persona locks and the effective tier floor decide eligibility, then
tier, capability scoring, preference rank, non-native-first, and the preferred
persona order the eligible captains; the persona default captain and the
Premium slot reserve also apply. It works without a Jev key. **Smart Routing**
(`modelTier.usageRouting.enabled` true) keeps that order and adds the usage
filter (Exhausted removed, Low and Reserve demoted), per-persona `default`,
`lighter`, and `stronger` model lists (`modelTier.usageRouting.personaModels`),
the `capacity_escalation` typed decision that chooses which list goes first,
and optional `personaRoutes` that restrict a persona to named accounts. Routes
never order captains, and nothing in Smart Routing changes the tier floor. See
[Smart Routing](#smart-routing).

A `modelTier.usageRouting` account can also own a separate captain login.
Set `runtime` plus `homeDirectory` (ClaudeCode `CLAUDE_CONFIG_DIR`, Codex
`CODEX_HOME`, OpenCode `XDG_DATA_HOME`), or `launchCredentialEnv` or
`launchCredentialFile` for Cursor (`CURSOR_API_KEY`). The Routing tab's
**Subscription accounts** section creates any number of accounts per runtime
under `<data directory>/accounts/<id>`, runs each runtime's login from the
browser (Codex device code, Claude Code sign-in with a pasted code, OpenCode and
Cursor API keys), assigns or clones captains, refreshes one account's usage on demand (bypassing
`refreshIntervalMinutes`, still honouring a provider retry-after), and deletes
an account with no captains together with its server-derived folder; see
[Logging in from the Dashboard](#logging-in-from-the-dashboard). An account without those fields launches its captains on
the shared login, as before. A missing login blocks the account with a named
reason. Claude Code and Codex accounts also run the runtime's login status
command in the background, so an expired or revoked login reads
`account_login_expired`. When such an account removes every remaining captain, the
routing decision reason (usage preview `reason`, and the deferred-mission log)
is that account code. A quota, billing, or authentication failure on one captain holds the
whole account Exhausted and quarantines its idle captains until the retry time.
Rollout of any second subscription account needs an owner decision under the
provider's terms. See [Account logins](#account-logins).

### Requested captain and fallback tier

A mission can store a requested captain (`RequestedCaptainId`) and a
fallback tier (`Tier`). They come from the mission itself, a voyage captain
override (`captainAssignments` with `captainId` and `fallbackTier`), or a
persona `DefaultCaptainId`. Every assignment path applies one rule:

1. The captain pool keeps only captains that are Idle, in the mission's
   tenant, not quarantined, not excluded after a policy refusal, and not
   reserved by another assignment. When Smart Routing is enabled, the pool
   also drops captains outside the persona's routes and captains the usage
   filter removes (Exhausted or at the account concurrency limit). No request
   overrides these gates. A demoted (Low or Reserve) requested captain is
   still assigned.
2. If the requested captain is in that pool, it is assigned. This is an
   explicit choice. It wins over persona preference, model-tier selection
   and the captain's `AllowedPersonas` fence.
3. If the requested captain is not in the pool, normal routing runs over the
   captains at or above the fallback tier. The fallback tier is the stored
   `Tier`, or the requested captain's own effective tier when no tier is
   stored. The lowest tier at or above that floor is preferred. A stored tier
   with no requested captain applies the same floor.
4. If no captain meets the floor, the mission stays Pending with
   `WaitingForIdleCaptain`. It is never given to a lower-tier substitute.
5. If the requested captain no longer exists and no tier is stored, normal
   routing applies.

Rules 3, 4 and 5 record a `mission.requested_captain` event. The event names
the requested captain, why it was not used, and the tier. Read these events
when a mission with a requested captain waits. A wait that does not change is
recorded once. A mission with neither field set is assigned exactly as
before.

With no pinned tier on any captain, each captain's tier is classified from
its model name, and a model the classifier does not know is Standard.

`factory/settings.fleet.example.json` holds the fleet routing policy, guard,
and reviewer assets. Captain tiers and ranks are not settings: set them on the
records, or let the one-time migration derive them from a settings file that
still carries the retired tier keys. Persona minimum tiers are also record
fields. The live
`~/.armada/settings.json` is not in the repository.

The `docker/` image pins (agent CLI set, `CLI_REFRESH`, `@latest`) are
project infra for this deployment. They are not product defaults. Gate
them with build args when you ship a generic image.
