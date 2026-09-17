---
topic: "Configuration And Administration"
summary: "Administrator surfaces: migrations, Harbor, per-captain credentials, the AI-Memory folder, the typed-decision system, and model routing policy."
read_when: "Changing settings, routing, migrations, or the typed-decision configuration."
applies_to: orchestrator
tier: leaf
---
# Configuration And Administration

Treat fleet, vessel, captain, persona, pipeline, playbook, runbook, workflow
profile, environment, template, backup, and server-stop tools as administrator
surfaces. Read the existing record first. Use
`armada_audit_operational_assets` before and after asset changes. Validate
provider models with a live provider call before putting them in a tier.

### Schema migration ledger

Each provider driver applies only migrations above the highest version in
`schema_migrations`. A migration numbered below an applied version would
never run. This happens when parallel branches land out of numeric order.
Every driver therefore reads the ledger through one shared rule before any
migration, schema guard or prerequisite step. If a known migration below the
applied maximum has no row, startup fails with
`SkippedMigrationVersionsException`. The message starts with
`skipped_migration_versions:` and names the provider, the applied maximum and
each missing version. Nothing is written, so every restart refuses the same
way. Version numbers retired from the code's list are not gaps, and neither
are ledger versions the code does not know.

The runner refuses; it does not apply the missing migration late. Reasons:

- A late migration runs against a schema its author never saw. The higher
  versions may have dropped, renamed or backfilled objects it touches, and a
  data statement can then succeed with the wrong result. Nothing reports that.
- A fresh install applies the same versions in the other order, so fresh-
  install schema checks no longer describe the upgraded database.
- MySQL DDL commits statement by statement, so a late migration that fails
  partway leaves partial schema outside a transaction.
- Per-version schema guards (model endpoints, captain endpoint links, Harbor
  enrollment) and the migration checkpoints assume ascending application.

Interrupted migrations do not trip the rule. Each migration records its row
only after its last statement, so an interrupted run leaves the maximum below
it, and the restart resumes normally.

The fix belongs in source, before the migration ships: renumber the unapplied
migration above the highest version any deployed database has applied, on
every provider. A self-deploy candidate check or an isolated-restore boot runs
the same startup, so it reports this refusal and leaves the running admiral in
place. If a build with the lower number already ran against a database, the
owner decides the recovery. Restore from a backup taken before the higher
version, or apply and record the migration by hand after review. Never insert
a ledger row without its schema change.

`scripts/common/verify-fork-migrations.py` is the source-side check. It
refuses a new declaration at or below the fixed manifest baseline. It also
hashes every class member that supplies statements to a protected declaration
on any provider, including shared schema classes and the MySQL and SQL Server
initial statements, and names the member when its body changes, it disappears
or it is renamed. A new member with a new migration passes. `--explain` says,
for each changed declaration, whether only its description or member names
changed while the statement content stayed the same; such a change still fails
until a reviewed manifest rewrite accepts it. It cannot
see two unlanded branches that both number above that baseline; the startup
rule covers that case.

### Harbor runners (disabled by default)

`Harbor.Enabled` defaults to false. While false the Admiral registers no Harbor link route and no Harbor
enrollment routes, and local captain execution is the only execution path. Keep it false on the production
Admiral until the Harbor acceptance is recorded; the current container deployment needs no change.

To evaluate Harbor on an isolated Admiral, set `Harbor.Enabled` to true in `settings.json` and restart.
`WebSocketEnabled` must stay true, or the Admiral logs that Harbor was not registered. The link path
(`Harbor.LinkPath`, default `/harbor/link`) is served on the existing REST port, so the container's published
port and TLS termination apply unchanged. Enroll each runner against a bearer credential with
`POST /api/v1/harbor-runners/enrollments` and revoke with `.../{runnerId}/revoke`.

The link authenticates only through the standard credential headers; tenant, user and access-key headers are
ignored. Each job is bound to one runner, its enrollment generation and its connection generation, so a stale or
foreign link cannot report for it. Every job frame and heartbeat is revalidated against durable enrollment, so a
runner revoked or re-enrolled on any Admiral is refused by name on its next frame and its link closes. The wire
contract is `docs/HARBOR_PROTOCOL.md`; ownership and revocation are `docs/HARBOR_IDENTITY.md`.

Mission launch stays local unless a route opts a captain or a vessel in. `Harbor.MissionRoutes` entries name a
`RunnerId` and either a `CaptainId` or a `VesselId`; a captain route wins over a vessel route. The mission owner
must be the runner's enrolled tenant and user. The runner works in the Admiral's dock path unchanged (a shared
mount), or under `RunnerWorkingDirectoryRoot` when `AdmiralWorkingDirectoryRoot` is also set. A routed launch is
refused by name and never falls back to a local process: owner mismatch, a captain of another tenant
(`harbor_captain_tenant_mismatch`), an account-login captain, a provider-key
captain (its variables may not leave the Admiral), a dock outside the directory map, or Harbor execution not
registered. The dock, branch and landing stay on the Admiral. A Harbor job appears to process liveness, stall
detection, stop and recovery as a synthetic process id, so a runner job that is lost reads as a dead process.

Jobs are durable. At start the Admiral fails every job an earlier process left unfinished
(`harbor_admiral_restarted`). A runner disconnected longer than `Harbor.DisconnectedJobGraceSeconds` loses its jobs
(`harbor_runner_disconnected`). List, inspect and stop jobs with `GET /api/v1/harbor-runners/jobs`,
`GET .../jobs/{jobId}` and `POST .../jobs/{jobId}/stop`, or the MCP tools `armada_harbor_jobs`,
`armada_harbor_job` and `armada_harbor_job_stop`. Enabling mission routes in production needs a separate,
owner-approved rollout.

### Runtime MCP startup

Ask starts a separate temporary runtime with its own MCP launch configuration.
The dashboard calls the captain tools endpoint with `context=ask` to check the
planned endpoint. A successful probe proves tool discovery from the server,
not a connection from a running chat process. Failed and empty probes show
the returned reason. See [Ask MCP availability](../reference/ask-mcp.md).
An API-endpoint captain has no MCP client: its tools response lists the
built-in workspace tools it actually runs, and Ask applies the launch endpoint
admission rule before starting it.

**Cursor captains need `--approve-mcps`.** cursor-agent discovers a workspace
`.cursor/mcp.json` but leaves its servers "not loaded (needs approval)" in a
non-interactive `--print` run; `--trust` covers the workspace only. The runtime
passes `--approve-mcps` so the dock's Armada server loads. Proof is a Research
smoke mission whose report lists the Armada server by name; the file on disk
proves nothing by itself.

**Send Claude prompts on stdin.** `claude --mcp-config` accepts multiple
values. A positional prompt after it can be read as another config path.
OpenCode accepts the prompt as its `run` argument.

### Per-captain provider credentials

A captain whose model is served by an external provider (a `provider/model` id such as `example-provider/claude-fable-5`) normally uses
the provider's host-level environment variable (for example `EXAMPLE_PROVIDER_KEY`). A captain may instead carry
its own `apiKey` (and optional `apiBaseUrl`) on its record, which wins over
the environment variable. This lets captains on separate provider subscriptions
run side by side on one Admiral; burn down each subscription, then delete its
captains. The MCP captain surface returns the key masked (last four
characters preserved); the dashboard keeps the raw value so the edit form can
prefill it, and entering the key in the dashboard never passes it through an
orchestrator. Creating a captain whose model is not entitled on the
environment key persists with a warning instead of failing, so a credential
that arrives after creation can be attached from the dashboard.

Vessel instruction files and generated briefing files are protected paths.
Captains must propose instruction changes. The orchestrator reviews and applies
them outside the mission dock.

### AI-Memory repository folder

When `aiMemoryRoot` is set, every captain brief carries a Shared Memory
section. It names the vessel's own folder under `<aiMemoryRoot>/repos/` when
one resolves. The admiral reduces the vessel name and each folder name to
lower-case letters and digits and compares them, so vessel `SomeVessel`
resolves to folder `some-vessel`, `somevessel`, or `Some_Vessel`. The brief and
the `deferred-facts.md` lookup use the folder's real name.

- No match: the brief says the vessel has no folder, and the admiral logs it
  at Info once per vessel per process.
- Two or more folders reduce to the same name: no folder is chosen, the brief
  names the ambiguity, and the admiral logs it at Warn. Rename or remove one
  folder.
- The root cannot be read: no folder is chosen, the brief and a Warn log line
  give the error, and the dispatch continues.

### Typed decisions

The typed-decision system (TypeSafe Jev) is an advisory classifier the admiral
can consult at a decision point. The global `mode` ships `Gate`, and the system
is inert until the key is present: with no key the null client answers every
call as unavailable, so every decision runs its deterministic rule exactly as
before. Per decision group:

- Every decision ships in `Gate` at its threshold (`0.90` unless stated below)
  and records the rule's verdict and the model's on every call, so a post-gate
  review can move a threshold or set one decision `Off` without a deploy.
- `leak_hunk` and `log_watch` are design documents with no adapter yet, so their
  `Gate` setting has no effect until one is wired.

The safety contract holds whenever it is enabled:

- The model never approves a Judge PASS, never lands, never dispatches, and
  never silences an alert. Its gated action can only make recovery **more
  conservative** (for example, hold a rescue that the rule would have run).
- It never converts a rule hard-block into a rescue: a deterministic block
  always wins.
- It gates only at or above the decision's confidence threshold; below it, the
  rule stands. Gate is never an approval: at any confidence the model cannot
  approve a Judge PASS, land, dispatch, delete, or write memory. A gated answer
  can only take the more conservative action the decision defines (hold, flag,
  escalate, annotate, or order).
- Every call is bounded by the settings timeout, linked to the caller's token,
  fails closed to the rule, and never throws into the caller. A slow decision is
  unavailable, not late.
- Nothing egresses unredacted. `DecisionStateRedactor` removes Armada ids,
  absolute paths, hosts, URLs, commit hashes, and key-shaped tokens, then
  truncates to the state cap. The Bearer key is read from the environment only.

Configure it under `typedDecisions` in `settings.json` (see the README settings
table). The global `mode` is `Off`, `Shadow`, or `Gate` and is the single kill
switch; each entry in `decisions` has its own `mode` and `gateThreshold`, and
the effective mode is the minimum of the two. `Shadow` consults the model and
records the answer while the rule stands; it is also the demotion target for a
decision operators reverse too often. Every decision ships in `Gate`, but the
system is operationally off until the key is confirmed in the container (no key
means the null client). `mode` is
hot-reloaded — a change takes effect without a restart and survives an MCP
settings write.

Every enabled call emits one event: `typed_decision.gated` when a gate at or
above threshold changed the outcome, `typed_decision.shadow` when the rule stood
(a Shadow-mode call, or a Gate-mode call below the threshold), and
`typed_decision.unavailable` otherwise. Each event carries the decision, the
rule verdict, the model version the provider reported (`model`), the model's
answers with their confidences and probability distributions, tokens, latency,
`batch_size`, the gate outcome, the provider's redacted explanation when it
rejected the request (`unavailable_detail`), and the state's SHA-256 and byte
count — the state itself is never recorded. Read the flow with:

```sql
select payload from events where event_type like 'typed_decision.%';
```

The state goes out as a JSON object: the redactor redacts every property name
and string value in place and, when the object exceeds `maxStateChars`,
shortens the longest strings (keeping head, tail, and `[ARMADA:` marker lines)
until it fits. Decisions that ask about several independent items —
`criteria_lint`, `inbox_triage`, `memory_candidate`, `followup_routing`,
`memory_review`, and `owner_digest` — share requests: items are packed in order
into requests of at most 100 questions whose combined state stays within
`maxStateChars`, and each item is still gated and recorded on its own event
(with an even share of the request's tokens). `papercut_merge` still asks per
pair, because each comparison depends on the merges before it.

A synthetic evaluation set checks the decisions that gate the recovery, review,
and Linter seams (`failure_cause`, `refusal`, `runtime_failure`,
`review_substance`, `lint_finding`). Each case builds its requests through the
decision's own adapter, so it tests the exact state and questions production
sends. A reference case pairs two variants that differ in one relevant fact and
states the answer for each; a consistency case changes a fact that must not
matter and requires the answers to agree. Operators run it with
`armada_typed_decision_eval`; it also runs in the background whenever the
provider reports a model version that has not been evaluated
(`typedDecisions.evalOnModelChange`, default `true`). Each run records one
`typed_decision.eval` event with the model and each case's outcome. A failing
case is a finding to review — a threshold, a question, or the model — not a
build failure.

Two decision points are described as design documents before any code lands,
reviewed by the owner before implementation:
[`leak_hunk`](design/typed-decision-leak-hunk.md) (an advisory per-hunk leak
classifier behind the deterministic dock-boundary scanner) and
[`log_watch`](design/typed-decision-log-watch.md) (a read-only screen over a
running mission's log that posts a voyage-tagged board note and a
`captain.course_flag` event). Neither blocks, stops, or dispatches; each only
flags.
The typed-decision system is also offered to captains directly, through four
mission-scoped MCP tools next to the memory tools: `armada_typed_decision` and
its three pre-shaped helpers `armada_check_premise` (a captain checks its own
reading of the task before it starts), `armada_memory_triage` (the Recorder
triages a memory candidate before writing it), and `armada_check_prior_art` (a
captain checks whether the work already exists before it writes a type — the D26
retrieval plus typed answers for its stated plan). Authority does not travel with
the tools. Each call redacts its state before egress, is bounded by the
per-mission call budget in `typedDecisions.captainTool`, writes exactly one
`typed_decision.captain` event carrying only the state hash and byte count, and
has no side effect on any Armada record — it dispatches nothing, lands nothing,
edits no objective, and writes no memory. The tool is enabled by default
(`typedDecisions.captainTool.enabled` is `true`); setting it `false` makes every
call return `unavailable`. Each helper also follows its own decision
(`premise_check`, `memory_record`, `prior_art`). See `docs/MCP_API.md`
for the tool arguments.
Each wired decision holds an adapter over the shared client, never the raw
client, and every adapter follows one skeleton: Off returns the rule with no
call; unavailable returns the rule; Shadow or below threshold returns the rule
and records a shadow event; at or above threshold it combines the rule and the
model, where a rule hard-block always wins. The four wired today are:

- `failure_cause` — autonomous recovery consults the model only for a failure
  the rule would rescue; the repeated-identical-test and Infra/Timeout blocks
  stay hard-blocks that win first. The state joins the voyage Checks and the
  parent's failing test names. A contention, provider, environmental, or
  verdict-form read at or above threshold, or a very likely repeat, holds the
  rescue for operator review as `typed_decision:<cause>`.
- `refusal` — the structured refusal marker and a provider safeguard block stay
  authoritative; the model may promote a prose refusal the phrase rules missed
  or demote a quoted phrase at very high confidence. The criteria state the
  domain: authorized heavy-duty vehicle diagnostics, where seed-key exchange and
  UDS SecurityAccess are ordinary engineering, never a refusal. In `Gate`, a
  `blocked_on_premise` outcome at or above threshold files a
  `BriefContradiction` papercut for the mission through the same parser path a
  captain's own `[ARMADA:PAPERCUT]` line takes. Its detail is the output tail
  after the typed-decision redactor removes ids, paths, hosts, hashes, and
  key-shaped tokens. The refusal verdict does not change.
- `runtime_failure` — only a bare Crash is offered for change, and only ever
  upgraded to the more conservative UsageLimit or AuthFailure; a recognised
  signature is never downgraded and a crash is never read as clean. In `Gate`,
  a `fleet_wide` reading at or above `0.9` (whatever the kind) records a
  `provider.account_fault_suspected` event and posts a broadcast board note.
  Both name the captain key family (the runtime plus the credential source:
  `model-endpoint`, `captain-key`, or `runtime-login`), never the key. This
  path only reports. It never benches, quarantines, or stops a captain.

One decision point reads the objective dispatch preview:

- **`preflight`** (ships `Gate`, threshold `0.80`) runs in
  `ObjectiveDispatchPreviewService` **after** the deterministic preflight block,
  so the deterministic facts (Q1/Q2/Q3/Q10/Q11) and the
  `objective_preflight_incomplete` Error issue are settled first and stand
  regardless of the model. It asks the text-half battery the code cannot settle:
  one noul per question, each phrased as the DEFECT, for Q1 (the premise versus
  the deterministic facts), Q4, Q5, Q6, Q7, Q8, Q9 and Q12, plus a Q13 choice
  `{none, needs_owner_ruling, needs_repo_fact}`. Its state carries the title,
  description, acceptance criteria, non-goals, refinement summary, Kind, vessel
  name, pipeline stages, and the deterministic `facts`, redacted before egress.
  In `Gate` a question at or above the threshold adds an Error issue
  `objective_preflight_model_flag` — the autonomous scheduler already skips on any
  Error issue and its `objective_scheduler.skipped_dispatch_preflight` event lists
  the code — and a Q13 `needs_owner_ruling` also posts one owner-addressed board
  note. The flag is preflight-class: an operator `armada_dispatch` with
  `forcePreflight: true` passes it, and the `objective.preflight_overridden`
  event names the flagged question numbers. The model only ADDS issues; it never dispatches, never lands, and never
  removes a deterministic issue. Below the threshold, unavailable, or `Off`
  leaves the deterministic preview unchanged (`Shadow` adds the flags as advisory
  `preflight_q<n>_model` warnings instead).

- `review_substance` (D4, ships `Gate` at 0.85) — the Judge-PASS structural
  validator (the required-heading regex plus the narrative-length floor) stays
  the rule and the fallback. The model asks one substance Noul per required
  review section and a `substantiated` Score. It only ever makes the outcome more
  conservative or accepts a form-only miss: when the rule rejected a PASS **only**
  because a heading failed the regex and every section's substance is present at
  threshold with the review at or above `evidenced`, the model accepts it as
  `heading_form_only` into the same independent Check gate the rule would have
  run; when the rule validated a PASS whose substance is thin (`substantiated` at
  or below `partly evidenced`), it **holds** the PASS for operator review. The
  hold is real: the mission records `HeldForOperatorReview` and its reason, a
  mission-activity line and a `typed_decision.gated` event surface it, and the
  PASS stays validated so the Check gate still runs. A PASS the Check gate
  accepts then stays `WorkProduced` with its dock kept: the Judge completion path
  neither hands it off nor lands it, the landing handler refuses it, and the
  landing drain does not count its review chain as passed, each logging the
  hold reason. The inbox lists it as `judge_pass_held`. Only an operator resolves
  it with `armada_review_hold`: `clear` lets the PASS proceed through the normal
  handoff or landing path and records `mission.hold_cleared`; `fail` fails the
  mission, cancels its dependent stages and records `mission.hold_failed`. Both
  events name the operator and the reason. Nothing clears a hold automatically,
  and a new completion attempt of the mission is judged afresh. A rejection on a
  real ground (empty output or a too-short narrative) is never overturned, and a
  validated PASS is never auto-failed. The model never approves, lands, or
  dispatches.

Two decision points recover captain time at the pipeline level (both ship
`Gate`):

- `stage_necessity` (D19) sits on the dispatch preview's resolved pipeline
  stages and lets the model propose which NON-Judge stages an objective does not
  need — a TestEngineer on a docs-only Chore, a Usability Engineer on a protocol
  port. The Judge is never a skip candidate and is never marked. Below the
  threshold a stage is retained; at or above it the stage is listed as a
  `stage_optional` Warning the operator confirms through `skipStages` before the
  voyage is materialised; only at or above `0.95` is a stage marked auto-skip. The
  model never removes a stage by itself below `0.95` and never proposes the Judge.
  With the decision `Off` the preview lists every stage. Auto-skip is not taken:
  a stage is dropped only when an operator names it in `skipStages` at dispatch
  or in a confirmed `preparation.stageSkip` (see
  [05 Standard workflow](05-standard-workflow.md)).
- `handoff_outcome` (D20) sits on the stage handoff, before the next mission's
  brief is frozen, and turns "failed at the Judge after four stages" into "held
  after one". A `blocked_missing_context`, `blocked_owner_question`, or
  `off_premise` outcome at or above threshold **halts** the voyage before the next
  stage: the pending dependents are cancelled with reason
  `handoff_blocked:<outcome>`, one incident is opened carrying the finished
  stage's output as the question text, an owner-addressed board note is posted for
  a blocked owner question, and the finished stage's branch is preserved. A
  `partial` outcome does **not** halt; it Mails the next stage the unmet
  acceptance criteria and the voyage continues. The decision never approves work,
  never lands, and never bypasses the Judge — a halt opens an incident rather than
  passing work through, and work that reaches the Judge is still judged. With the
  decision `Off` the deterministic handoff stands.

Three persona-specific decision points sit on Judge and handoff seams (all ship
`Gate`):

- `revision_kind` (D21) sits after `ParseJudgeVerdict` on a NEEDS_REVISION's
  revision items, before autonomous recovery classifies the failure. The model
  answers a `kind` Choice {behaviour, test, comment_only, doc_only, boundary} per
  item and a voyage-level `all_non_behavioural` Noul. When every item is
  non-behavioural at or above threshold and no item is a behaviour or test change,
  the seam marks the failure `revision_comment_only`, so autonomous recovery
  **holds the rescue** (a comment-only NEEDS_REVISION is an operator landing, not
  a rescue chain), and opens an incident tagged for operator landing. A single
  behavioural or test item leaves the rule standing and the rescue proceeds. The
  model never lands; the finished work stays on its branch for the operator.
- `test_covers` (D22) sits on the TestEngineer handoff, over the added test
  methods and the objective's symptom sentence. The model answers `covers_symptom`,
  `asserts_source_text`, and `would_fail_before_fix` Nouls per added test; a
  doubted test becomes a Judge **instruction** prepended to the next brief ("verify
  test X fails without the change"). It **never fails the stage** by itself. With
  the decision `Off` the handoff is deterministic.
- `lint_finding` (D24) sits on the Linter handoff, over each finding the Linter
  emits. The model answers a `class` Choice {correctness, safety, consistency,
  style_preference, false_positive} and a `severity` Score [cosmetic, should_fix,
  must_fix, blocks_merge] per finding. Only correctness/safety findings at
  `must_fix` or above reach the Judge as **blocking**, and a `style_preference`
  finding becomes an **evidence note**; a routing note is prepended to the next
  brief. The Linter's own result is unchanged — only the routing is. With the
  decision `Off` the Linter output flows unchanged.

Two decision points read the papercut grouping:

- **`papercut_merge`** (ships `Gate`) runs at listing time
  (`armada_list_papercuts`, grouped mode). It asks whether two groups of the same
  vessel and category describe the same underlying issue and, at or above the
  threshold, folds them into one row **in the listing only** — no stored papercut
  event is changed and no group is deleted, so setting the decision `Off`
  restores the plain grouping. It considers only the largest groups per vessel
  and caps the model calls per listing. A merge records a `papercut.merge_proposed`
  event (recorded but not applied in `Shadow`). Each pair call follows the same
  skeleton as every other adapter: the listing call's token reaches the client
  (the call is bounded at two minutes), and a timeout, provider error, or thrown
  exception records `typed_decision.unavailable` and returns the plain grouping.
- **`memory_candidate`** (ships `Gate`, threshold `0.90`) runs in the weekly
  papercut sweep. At most once per seven days the health loop groups the
  papercuts reported in the last seven days, applies the papercut merge, and offers the
  largest repeated groups (two or more reports, at most 20) to the decision. In
  `Gate` at or above the threshold it stores a **memory proposal** in the
  database for the owner to promote or an operator to dismiss. The AI-Memory
  folder is read-only to the admiral, so nothing is written there: the model
  never writes memory and never dismisses. Proposal text is redacted, the
  subject is stored only as a SHA-256 fingerprint, and a subject already
  proposed (open or dismissed) is not proposed again. While the decision is
  `Off` the sweep reads nothing and calls nothing.

One decision point reviews what the Recorder stage wrote:

- **`memory_review`** (ships `Gate`, threshold `0.90`) runs when a
  Recorder-stage mission finishes its work. It reads the native memory records
  that mission wrote (at most 10) and asks the four memory-review questions for each:
  `type_ok`, `duplicate_of` (a choice among at most five existing records of the
  same vessel and type or topic), `will_go_stale`, and `belongs_in_ai_memory`.
  In `Gate` at or above the threshold it may only **lower** salience (to `0.2`
  for a duplicate, `0.3` for a stale or wrongly typed record), **link** a
  duplicate to the record it repeats with a `duplicate-of:<memory id>` tag, and
  store a memory proposal (source `recorder_seam`) for a record that belongs
  in AI-Memory. It never deletes a record, never changes content, summary, type,
  topic, or key, and never raises salience. An unavailable model stops the pass
  with every record unchanged; `Off` calls nothing. Each reviewed record records
  one typed-decision event scoped to the Recorder mission.

Operators read and close proposals with `armada_list_memory_proposals` (filter
by `state`) and `armada_dismiss_memory_proposal` (`id`, `reason`, `operator`).
Both are operator tools, outside mission scope, and refuse any caller other than
a global administrator. The store is the `memory_proposals` table (migration
SQLite 104, PostgreSQL 105, MySQL 96, SQL Server 99). See `docs/MCP_API.md`.

Two operator-side decisions gather owner decisions and pre-fill the corpus:

- **`owner_digest`** (ships `Gate`) is a scheduled runner shaped like the
  health loop. Once per UTC day it collects the owner-decision candidates its
  hit source found — an owner-decision preparation claim an anchor change
  re-opened (`NeedsRecheck`) on this tip, with preflight question 13 `needs_owner_ruling`
  flags, `owner_ruling` hits, and board notes inbox triage classified as questions
  attaching as those signals land — ranks each with a `cost_of_waiting` Score
  `[none, a lane idles today, a captain is guessing now, a landing is held]`
  and a `default_safe` Noul, and posts **one** owner-addressed board note plus
  **one** `owner_decisions.digest` event listing the questions by cost, each
  with its proposed default. The runner is dormant while the decision is `Off`,
  so it is never forced on; it is a no-op on any day with no candidates. It
  **never answers** a question — the deterministic cost from the fan-out and age
  is the fallback, a gated model reading may only ESCALATE that cost, every
  proposed default is a suggestion the owner still records on the row, and the
  digest event carries only ranking metadata (counts, cost levels, sources),
  never the question text.
- **`corpus_prelabel`** (ships `Gate`) is an operator-side helper script,
  `scripts/autonomy/draft-corpus-line.mjs`, run outside the admiral. It drafts
  one decision-corpus line (the schema in `AI-Memory/corpus/README.md`) from an
  incident, a mission failure reason, a Mail signal, or a preflight result, so
  the operator does not start the capture rule from a blank line. Its single
  hard guarantee is that every line it emits carries `"draft": true` and nothing
  it emits is a confirmed line: the operator confirms a draft by removing the
  flag, and the script never removes it and never pre-fills a decision — only
  the fields the corpus rule already settles (a preflight line's
  `preventable_in_brief` follows from its failed questions) are set. Run
  `node scripts/autonomy/draft-corpus-line.mjs --input <file.json>` (or pipe the
  input object on stdin), optionally with `--out decisions.jsonl` to append the
  draft; `node scripts/autonomy/test-draft-corpus-line.mjs` is its self-check.
Three platform-side decisions ship `Gate`:

- **`flake_score`** runs in `DefinitionOfDoneGate` after
  `DefinitionOfDoneFailureClassifier` classifies a failed unit-test command. It
  asks a `flake_likelihood` Score `[deterministic, likely real, likely load,
  known flaky family]` and an `outside_diff` Noul over state carrying the failing
  test names, the assertion lines, the touched files, whether the same tests
  failed on another branch in the last 24 hours, and the classifier's class. In
  `Gate`, a `likely load` or `known flaky family` reading at or above threshold
  triggers an isolated, class-filtered re-run of only the failing classes; the
  re-run's real result is the truth (a pass clears the red, a failure leaves it
  red). The model never marks a red check green — only a genuine passing isolated
  re-run does — and a re-run runs only for a `dotnet test` command that can be
  isolated; otherwise the red stands unchanged.
- **`routing_hint`** is Smart Routing only (owner decision 2026-09-16): it is
  never wired into the legacy tier selector. A route gains an optional `shapes`
  tag list (a tagless route matches every shape, so existing configs are
  unchanged). The model answers a `shape` Choice, a `policy_sensitive` Noul, and
  two context Nouls; the hint reorders — never re-selects — the routes V2 already
  approved and found eligible: among eligible routes for a routine mission in the
  Normal state it prefers the first route whose `shapes` contains the chosen shape
  at threshold, and `policy_sensitive >= 0.9` prefers a `policy-tolerant` route
  (falling back to the V2 default and recording `no_tolerant_route` when none is
  configured). Reserved personas and non-Normal account states are never
  affected; every hard V2 constraint runs after the reorder. See
  `docs/USAGE_ROUTING.md`.
- **`change_substance`** sits over the extension-based
  `ChangeSubstanceClassifier`, which stays the rule. The model reads the rescue's
  added hunks and answers a `substance` Choice `{behaviour, test_only, docs_only,
  build_config, generated}` and a `risky` Noul. In `Gate` it may RAISE a
  documentation-only (or empty) extension reading to `Substantive` for the
  ineffective-rescue decision (`RescueEffectivenessEvaluator`), so a behaviour
  change is not failed as prose, and a `risky` reading at threshold adds one
  `CriticalTriggerEvaluator` escalation reason. It NEVER lowers a classification.
One decision point reads a returned refinement summary:

- **`criteria_lint`** (ships `Gate`) runs in
  `ObjectiveRefinementCoordinator.SummarizeAsync` after the summary is finalized.
  It asks, per acceptance criterion, five nouls phrased as the defect: a presence
  test over an artifact the change itself commits, a pinned pass or skip total,
  something not observable from a dock, a criterion satisfiable by an empty diff,
  and a criterion that mixes two behaviours. In `Gate` above the threshold each
  flagged criterion contributes model-flagged `criteria_review` lines appended to
  the refinement summary the operator reads before ReadyForDispatch. The adapter
  NEVER rewrites, reorders, or removes a criterion; `Off`, unavailable, `Shadow`,
  and below-threshold append nothing.

Two operator surfaces read the attention triage:

- **`inbox_triage`** (ships `Gate`) runs in the `inbox` and
  `armada_coordination_read` MCP tools. It scores each inbox item and board note
  for how urgently a human is needed. In `Gate` above the threshold each item
  gains an `attention` label (`informational`, `today`, `this_hour`,
  `blocking_live_voyage`), each board note also a `noteKind` (`handoff`,
  `status`, `question`, `stop_sign`, `hold_notice`), and the response is
  re-ordered by attention. NOTHING is hidden, dropped, or dismissed; `Off`,
  unavailable, `Shadow`, and below-threshold leave the deterministic severity
  order and set no label.
- **`followup_routing`** (ships `Gate`) runs in
  `JudgeFollowUpService.CaptureAsync` (and the audit-tool backfill path) over each
  item of a Judge's Suggested Follow-ups section. In `Gate` above the threshold a
  `triaged_objective` home creates a Triaged objective with auto-dispatch OFF, an
  `evidence_note` home appends an evidence note, and a `duplicate_of_existing`
  home LINKS to an existing open objective (a `same_as` noul against the vessel's
  top open objectives) instead of creating one. A blocking item is only flagged
  for the operator; the model NEVER creates a voyage, dispatches, or lands.

One decision point asks "does this already exist?" with evidence, at three seams
and no new persona (ships `Gate`):

- **`prior_art`** answers "does this already exist?" so voyages stop
  re-implementing landed work, work on unlanded branches, or work on a `recover/`
  ref. It is a retrieval step plus typed questions inside stages that already run,
  not a new Judge stage. The **retrieval** is deterministic (the "contextual"
  half): it mines identifiers of five characters or more, type and method names,
  and file paths from the objective (or a captain's plan, or a diff's added
  types), searches the landed tip, unlanded mission branches, preserved and
  recovery refs, and open objectives, and assembles de-duplicated candidates
  capped at twelve and roughly twenty-four thousand tokens. The **model** answers
  only over those candidates (the "honest" half): a per-candidate `delivers`
  Choice `{same_capability, partial_overlap, related_only, unrelated}` and
  voyage-level `already_done`, `integrate_not_duplicate`, and `reimplements`
  Nouls, every answer tied to a candidate's `path:line` so the reader verifies the
  evidence — the model can only speak to a candidate retrieval already found, and
  `unrelated` is always available. It wires three seams:
  - **Preflight (extends D5).** In `Gate`, `already_done` at or above threshold
    adds an `objective_prior_art_found` Error issue listing the candidates (the
    operator closes or re-scopes the row); `integrate_not_duplicate` adds an
    `objective_prior_art_integrate` advisory appending the candidates as landed
    seams to consume; and, when `already_done` lands in the uncertain band
    (`0.4`–`0.7`) on a large objective, a `prior_art_analyst_stage_recommended`
    advisory suggests inserting a read-only PriorArtAnalyst research stage before
    the Worker. The adapter only ADDS issues; it never closes or re-scopes a row.
  - **Worker premise tool (`armada_check_prior_art`).** A mission-scoped captain
    MCP tool that runs the same retrieval for the captain's stated plan and returns
    the candidates with the typed answers before it writes a type. It is in the
    caller-scoped tool list, and the Worker prompt names it. A candidate on an
    unlanded branch or a preserved or recovery ref carries a bounded excerpt
    (at most 30 lines) read from that ref.
  - **Judge (extends D4).** On the Worker handoff, over the diff's added types and
    the retrieval results, a `reimplements` reading at or above threshold prepends
    a review INSTRUCTION to the next brief ("verify whether the diff should consume
    the candidate through a seam"); it is a review instruction, never a verdict,
    and the Judge still judges.
  With the decision `Off` every seam runs its deterministic path unchanged, and a
  seam with no retrieved candidate never calls the model.

### Vessel Workspace

The Workspace surface (dashboard `Workspace` page, `POST
/api/v1/workspace/vessels/{vesselId}/exec`, and the diff/file/tree/status REST
routes) operates on a vessel's configured working directory. Use it to browse,
edit, search, review a working-tree diff, or run a one-shot shell command.
Workspace shell commands run through the platform shell, are killed with their
process tree when they exceed the timeout, and are restricted to tenant
administrators. Every git invocation the workspace performs is bounded by a
30-second timeout that kills the process tree and suppresses the pager and
credential prompts, so a wedged git cannot hang the endpoints.

Backups can contain operational state. Store them in an approved location and
apply retention limits. Restore, delete, purge, stop, and bulk operations need
an explicit operator decision.

### Model routing and dispatch policy

Product defaults are empty and policy-neutral. A fresh `settings.json`
classifies no model family, reserves no specialist persona, selects
randomly among idle persona-eligible captains, and leaves the
stage-persona title guard off. Edit these keys in `settings.json` or on
the Dashboard Settings page:

| Setting | Hot-reload | Product default | Dashboard control |
| --- | --- | --- | --- |
| `modelTier.midTierModels` / `highTierModels` | Yes | empty | Mid-tier / high-tier model lists |
| `modelTier.familyClassificationRules` | Yes | empty | Family classification rules JSON |
| `modelTier.specialistPersonas` | Yes | empty | Specialist persona list |
| `modelTier.withinTierStrategy` | Yes | `Random` | Within-tier strategy |
| `modelTier.withinTierPreferenceOrder` | Yes | empty | Preference-order JSON |
| `modelTier.preferNonNativeFirst` | Yes | `false` | Prefer non-native first |
| `modelTier.usageRouting` | Yes | disabled, empty accounts | [Usage policy, account status, and preview](USAGE_ROUTING.md) |
| `modelTier.reservedHighTierSlots` | Yes | `0` | Reserved high-tier slots |
| `voyageDispatch.rejectStagePersonaTitlePrefixes` | Yes | `false` | Reject stage-persona title prefixes |
| `voyageDispatch.stagePersonaTitlePrefixes` | Yes | empty | Prefix list |
| `modelProviders` | No (startup) | empty | modelProviders JSON |
| `additionalPromptTemplates` / `additionalPersonas` / `additionalPipelines` | No (startup) | empty | Additional-asset JSON |

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
[Logging in from the Dashboard](USAGE_ROUTING.md#logging-in-from-the-dashboard). An account without those fields launches its captains on
the shared login, as before. A missing login blocks the account with a named
reason. Claude Code and Codex accounts also run the runtime's login status
command in the background, so an expired or revoked login reads
`account_login_expired`. When such an account blocks every approved route, the
routing decision reason (usage preview `reason`, and the deferred-mission log)
is that account code. A quota, billing, or authentication failure on one captain holds the
whole account Exhausted and quarantines its idle captains until the retry time.
Rollout of any second subscription account needs an owner decision under the
provider's terms. See [Account logins](USAGE_ROUTING.md#account-logins).

#### Requested captain and fallback tier

A mission can store a requested captain (`RequestedCaptainId`) and a
fallback tier (`Tier`). They come from the mission itself, a voyage captain
override (`captainAssignments` with `captainId` and `fallbackTier`), or a
persona `DefaultCaptainId`. Every assignment path applies one rule:

1. The captain pool keeps only captains that are Idle, in the mission's
   tenant, not quarantined, not excluded after a policy refusal, and not
   reserved by another assignment. When usage routing is enabled, the pool
   keeps only the captains that usage routing approves. No request overrides
   these gates.
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

When both tier lists and family rules are empty, every idle
persona-eligible captain is an equal peer. That is the vanilla dispatch
path. Low still maps to mid: that is the platform two-tier architecture,
not a fleet rule.

Copy `factory/settings.fleet.example.json` into the live settings file to
restore the former hardcoded routing, guard, and specialist-reviewer
assets. The live `~/.armada/settings.json` is not in the repository.
Merge the fleet overlay before you deploy this build if you need today's
behavior. Family rules now apply even when a settings object is supplied
(lists still win), so an unlisted version-bump that matches a seeded
family pattern classifies. That is a small expansion versus a
lists-only production file.

The `docker/` image pins (agent CLI set, `CLI_REFRESH`, `@latest`) are
project infra for this deployment. They are not product defaults. Gate
them with build args when you ship a generic image.
