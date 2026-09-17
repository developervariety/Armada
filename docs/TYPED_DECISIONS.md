# Typed Decisions

The typed-decision system: calibrated closed-question answers from a decision provider, used as a tie-breaker behind deterministic rules. This reference covers settings, modes, the kill switch, events, the captain tool, and every decision point.

The typed-decision system (TypeSafe Jev) is an advisory classifier the admiral
can consult at a decision point. It is **Off unless a provider key is
available**. Without a key the effective global mode is `Off` with reason
`typed_decisions_no_key`, whatever the stored `mode`: no decision calls a
client or records an event, and every decision runs its deterministic rule.
With a key, the stored global `mode` (default `Gate`) applies and each decision
runs at its own mode. Per decision group:

- Every decision ships in `Gate` at its threshold (`0.90` unless stated below)
  and records the rule's verdict and the model's on every call, so a post-gate
  review can move a threshold or set one decision `Off` without a deploy. This
  includes `capacity_escalation`, the Smart Routing model group choice.
- `leak_hunk` and `log_watch` are design documents with no adapter yet, so their
  `Gate` setting has no effect until one is wired.

**`capacity_escalation`** (ships `Gate`, threshold `0.90`) runs at assignment
under Smart Routing, only for a persona whose `personaModels` entry has a
`lighter` or `stronger` list. It asks one closed Choice (`lighter`, `default`,
`stronger`) over the persona, the title, the head of the description, and the
model lists. The answer only chooses which model group is tried first. Below
the threshold, `Off`, no key, a timeout, 429, 529, a parse error, or a client
fault gives `default`. The reading is cached per mission in memory for 30
minutes. The retired `routing_hint` decision and route `shapes` tags are
ignored when a settings file still contains them.

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
  truncates to the state cap. The Bearer key is never logged, recorded, stored
  in settings, or returned by any response.

## Retained training data

**Owner ruling 2026-09-17.** The REDACTED state of a decision call may be kept
on this host as the training and evaluation set for a local classifier. Only
retention changed; egress did not. An event still carries the state's hash and
byte count and never the state, and nothing extra leaves the host.

- **Two switches, both needed.** `typedDecisions.retention.enabled` turns the
  store on, and each decision opts in with its own `retainState`. Enabling the
  feature alone retains nothing, and turning it off is the kill switch for every
  decision. Both are read per call, so a change needs no restart.
- **Where it is kept.** JSON lines under
  `<data directory>/typed-decision-samples/<decision>/<date>.jsonl`. A line holds
  the redacted state, its hash, the rule's verdict, the model's verdict and
  confidence, the gate outcome, and the mission id. A file store, because the set
  is written once and read in bulk by a trainer, and a retention window is a file
  delete.
- **`retentionDays`** (default 90) bounds it; files outside the window are deleted
  at startup. **`minimumSamplesPerDecision`** (default 200) decides when a
  decision is reported as trainable.
- **Labels come from reversals.** `armada_typed_decision_reversal` records that a
  gated outcome was wrong and writes the corrected answer beside the call, joined
  by the state hash. That pair — what the rule said, what the model said, what a
  person decided — is the labelled example a local model is trained and reviewed
  against.
- **Retention never changes an outcome.** Every write is best effort; a failure is
  logged and swallowed, exactly like a recorder failure.
- **Report before you train.** `armada_typed_decision_labels` names every decision
  with retained data and says which are short of the minimum and by how much.

### The provider key

The key resolves in this order:

1. The environment variable named by `typedDecisions.apiKeyEnv`
   (`ARMADA_TYPESAFE_KEY`), when set.
2. The key file `<data directory>/secrets/typesafe-api-key`. The server derives
   the path; a client never supplies one. The folder is created `0700` and the
   file is written `0600`.

The client is resolved on every call, so adding or removing the key takes
effect without an Admiral restart. The startup log names the effective mode
and the key source, never the key.

The Dashboard has the same controls in **Settings > Typed decisions**
(administrators only): a banner "Off — no Jev key" when the effective mode is
`Off` for `typed_decisions_no_key`, the effective and stored global modes, key
presence and source, a password field that saves a key (the field is cleared
on submit and the key is never displayed), **Remove key** (the page says when
the environment variable still supplies a key), the global mode, and a table of
every decision with its description, mode, and threshold (0 to 1). **Save
modes** sends only the changed fields and reloads the status.

Administrator routes (the same permission as a settings write; never recorded
in request history):

| Route | Effect |
| --- | --- |
| `GET /api/v1/typed-decisions` | Effective global mode and reason, stored mode, `keyPresent`, `keySource` (`env` or `file`), and every decision's `key`, `mode`, `threshold`, and `description` |
| `PUT /api/v1/typed-decisions` | Body `{ "mode": "Gate", "decisions": { "<name>": { "mode": "Shadow", "gateThreshold": 0.9 } } }`; every field is optional. Unknown decisions, modes, and thresholds outside 0 to 1 return 400. Saved through the normal settings save. |
| `PUT /api/v1/typed-decisions/key` | Body `{ "apiKey": "..." }`; writes the key file and returns 204 with no body |
| `DELETE /api/v1/typed-decisions/key` | Removes the key file; `environmentSuppliesKey` is true when the variable still supplies a key |

`GET /api/v1/status` (`typedDecisions`) and `GET /api/v1/settings`
(`typedDecisionsStatus`) carry the same effective-mode summary.

Configure it under `typedDecisions` in `settings.json` (see the README settings
table). The global `mode` is `Off`, `Shadow`, or `Gate` and is the single kill
switch; each entry in `decisions` has its own `mode` and `gateThreshold`, and
the effective mode is the minimum of the two. `Shadow` consults the model and
records the answer while the rule stands; it is also the demotion target for a
decision operators reverse too often. Every decision ships in `Gate`. A shipped
decision missing from a stored `decisions` map runs at its shipped mode; set its
`mode` to `Off` to stop it. Without a key the effective mode is `Off` whatever
these values say. `mode` and `decisions` hot-reload in place — a change reaches
every decision point without a restart and survives an MCP settings write.

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
[`leak_hunk`](../archive/design/typed-decision-leak-hunk.md) (an advisory per-hunk leak
classifier behind the deterministic dock-boundary scanner) and
[`log_watch`](../archive/design/typed-decision-log-watch.md) (a read-only screen over a
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
the tools. Each call redacts its state before egress (there is no per-mission
call cap), writes exactly one
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
  domain: authorized engineering on owned systems, where authentication and
  access-control protocol code is ordinary engineering, never a refusal. In `Gate`, a
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
  [05 Standard workflow](armada-ops.md)).
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
Two platform-side decisions ship `Gate`:

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

## Custom decisions — operators define their own

Beyond the shipped decisions, an operator can define **custom typed decisions** from the
dashboard (**Settings > Typed decisions > Custom decisions**) or the settings API. A custom
decision carries its own questions and describes how to build its state, and it is stored in
`typedDecisions.custom` and hot-reloads in place like the built-in decisions.

A custom decision is **advisory by construction** and cannot break the safety contract:

- It runs only at a generic `surface` — `CaptainTool` (invoked on demand through
  `armada_run_custom_decision`) or `MissionDiff` (an advisory pass over a finished mission's
  diff and output). It never wires itself into recovery, a Judge handoff, or landing.
- Its `binding` is one of a fixed, non-approving list (`CustomDecisionSeamEnum`): today `None`
  (record only) or `MissionDiffFlag` (record a loud advisory flag event when it gates). No
  member lands, dispatches, approves a PASS, holds a rescue, or writes memory. Adding an
  approving action would change the owner non-negotiables and needs a new ruling.
- Its effective mode is the minimum of the global mode and its own, so the global kill switch
  caps it. It is created `Off`, so nothing runs until an operator turns it on.
- It gates only at or above its `gateThreshold`, and only its bound conservative action; below
  the threshold, in Shadow, or unbound, it records and does nothing else.

Each custom decision defines `questions` (choice, score, or noul, exactly like the built-ins),
`stateFields` (which mission fields the `MissionDiff` surface sends), a `description`, and
`retainState`. Every call records a `typed_decision.custom` event (or
`typed_decision.custom_flagged` when it flags), carrying the state hash and byte count, never
the state.

Administrator routes (same permission as a settings write):

| Route | Effect |
| --- | --- |
| `PUT /api/v1/typed-decisions/custom/{name}` | Create or replace a custom decision. Refuses a name that collides with a shipped decision, an invalid mode/threshold/surface/binding, a `MissionDiffFlag` binding without the `MissionDiff` surface, or a malformed question, with 400. |
| `DELETE /api/v1/typed-decisions/custom/{name}` | Remove a custom decision. Deleting one does not resurrect it from the seeds. |
| `POST /api/v1/typed-decisions/custom/install-seeds` | Install the built-in example custom decisions (`source_fidelity`, `safety_step_present`, `citation_resolves`) that are not already present. They ship `Off` and unbound. |

`GET /api/v1/typed-decisions` returns every custom decision under `custom`.

The example seeds are the registry-friendly members of the repo-specific decision queue in
`AI-Memory/typed-decision-eval/PROPOSED_DECISIONS.md`. The queue's blocking or state-heavy
members (`reflash_guardrail`, `consumer_break_direction`) stay bespoke code and are not custom
decisions.
