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
- Some shipped decisions reach no decision point yet, so their mode has no
  effect until one is wired. Each is declared with the reason it is inert in
  `TypedDecisionWiring.UnwiredDecisions`, and every status surface reports that
  reason beside the decision's mode, so a `Gate` that consults nothing is never
  read as enforcement. The `Typed Decision Wiring` suite fails when a shipped
  decision is neither consulted nor declared there, and when a declared entry is
  in fact consulted, so that list is the current answer rather than this page.
- `log_watch` is wired as the model pass of the read-only captain-log screen
  (`captainLogScreening`). The screen reads the tail; the decision reads it for
  drift. With the screen off it is never called, whatever its mode says.

Conversation compaction is not a typed decision. The API-endpoint runtime
applies a deterministic inbound prune and truncate-head (see README,
"API-endpoint compaction"). Harness captains keep their harness's own
compaction. Armada ships no harness compaction plugin.

**`capacity_escalation`** (ships `Gate`, threshold `0.90`) runs at assignment
under Smart Routing, only for a persona whose `personaModels` entry has a
`lighter` or `stronger` list. It asks one closed Choice (`lighter`, `default`,
`stronger`) over the persona, the title, the head of the description, and the
model lists. The answer only chooses which model group is tried first. Below
the threshold, `Off`, no key, a timeout, 429, 529, a parse error, or a client
fault gives `default`. The reading is cached per mission in memory for 30
minutes. The retired `routing_hint` decision and route `shapes` tags are
ignored when a settings file still contains them.

**`dispatch_staleness`** (ships `Gate`, threshold `0.90`) runs at voyage dispatch when a
vessel's code index is stale on indexable source, or when an update is already in progress
under `Block`. It asks one closed Choice (`proceed`, `refresh_inline`, `block`) over the
configured policy, the relevance counts, whether an update is in progress, the title, and a
head of the description. It never receives raw hunks. The deterministic policy is the rule
verdict, except a stale index that touches no indexable source always Proceeds. Combine never
introduces a wait the rule would not, and a `Block` rule always wins. Off, unavailable, and
below threshold keep the rule.

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
  truncates to the state cap. Key shapes come first and are the display
  redactor's own rules (`SecretRedactor`): labelled secrets (`password = ...`,
  `api_key: ...`, `token=...`), Bearer tokens, provider tokens by prefix (`sk-`,
  `gh*_`, `github_pat_`, `AKIA`, `xox*-`, `glpat-`), and the string value of a
  property named as a secret. So egress removes at least every key the display
  removes. A labelled rule can take a code assignment such as
  `token = GetToken();` with it; that loss is accepted, because an
  under-redaction is a leak. The Bearer key is never logged, recorded, stored
  in settings, or returned by any response. The host, hash, and key rules match
  by shape, not by any dot, hex run, or long token: a dotted name is a host only
  when its final label is a known public or internal TLD (a single-dotted source
  file whose extension coincides with a TLD is kept; the same name at two dots is
  a host); a hex run is a hash only when it mixes a letter with a digit or a
  commit cue introduces it; a base64 run is a key only on a digit, a `+`/`=`, or
  dense case transitions. So a source diff keeps its file names, namespaces, and
  symbols for the decisions that read it, while every real host, id, path, hash,
  and key is still removed. Narrowing is deliberate: an under-redaction is a leak,
  so the excluded shapes (vanity TLDs that collide with code member names, an
  scp-style remote's repo short-name, a cue-less short hash indistinguishable from
  a number) are the ones a URL or internal suffix catches by another route.

## Egress rules

Three rules decide whether a state leaves the host at all, and every path that
sends state asks the same ones through one guard (`TypedDecisionEgress.Refusal`):
the adapter skeleton, the standalone adapters (`prior_art`, `memory_review`,
`papercut_merge`, `memory_candidate`, `followup_routing`, `preflight`,
`criteria_lint`, `inbox_triage`), custom decisions, and the captain tools. A
standalone adapter that decides several items at once asks per item: a refused
item keeps its rule and records why, and the other items are still decided.

- **Vessel exclusion.** A decision whose state concerns an objective, a mission
  or a vessel that names a vessel in `egressExcludedVesselIds` sends nothing
  (`egress_excluded_vessel`). An objective counts through its `VesselIds`, so an
  objective that lists an excluded vessel beside an allowed target is refused;
  this covers the objective-scoped decisions (`preflight`, `prior_art` at
  preflight, `criteria_lint`, `stage_necessity`, `owner_digest`,
  `dispatch_staleness`) as well as the mission-scoped ones. The vessels are
  checked before any state is built.
- **Content markers.** A state whose UNREDACTED text contains one of the
  markers that apply sends nothing (`egress_excluded_content`). The check
  reads the state before redaction because the redactor replaces absolute
  workspace paths - where dock and sibling-checkout paths live - with a
  placeholder, taking a marker inside them along. The markers are the global
  `egressExcludedMarkers` unless the decision or custom definition sets its
  own list; an empty own list opts out, which suits a decision whose state is
  only brief or objective text (a brief names paths but does not carry their
  content).
- **A rejected request is retried once at half size.** Redacted state
  averages about 3.6 characters per provider token but reaches 1.9 on dense
  text, so a state inside the character budget can still exceed the
  provider's 32k-token state-plus-longest-question limit (64k for the whole request). Lowering the budget for everyone to
  fit the densest state would truncate about a third of real states to
  rescue a fraction of one percent, so a request the provider rejects
  (`http_400`) is retried once with the state re-redacted to half its size,
  and a rejected batch is split in half. Every other unavailable reason keeps
  the rule without a retry.

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
  confidence, the gate outcome, and the mission id. New answered calls also keep
  the returned model version, every answer and distribution, and request provenance.
  Provenance version 1 contains redacted provider-format question JSON, its hash,
  the original wire-question hash, and the complete request-body hash. The body
  and credentials are not retained. The question JSON covers the whole packed
  request; `batch_size` and zero-based `batch_item_index` identify the local item.
  An unwrapped request has a null index. Reconstructing a complete batch requires
  every retained item for that request; a lone item and hash do not restore missing
  sibling state. Events contain these hashes and the item
  index, never question text. Legacy or skipped calls have no provenance; a
  missing definition must not be filled from today's questions. Question
  redaction can change a retained definition; the separate wire hash makes that
  distinction explicit. A capture failure keeps hash-only request identity and
  records `capture_failed`; it does not replace an available answer. An answered
  client result without provenance records `not_recorded` on the event. A file store, because the set
  is written once and read in bulk by a trainer, and a retention window is a file
  delete.
- **`retentionDays`** (default 90) bounds it; files outside the window are deleted
  at startup. **`minimumSamplesPerDecision`** (default 200) decides when a
  decision reaches the sample-count minimum. This count alone does not establish
  independent labels, compatible question/model cohorts, or a held-out test set;
  all three are needed before training or threshold selection. The report exposes
  `MinimumSampleCountMet` separately. `Trainable` stays false because a count-only
  store cannot verify those requirements.
- **Labels come from reversals.** `armada_typed_decision_reversal` records that a
  gated outcome was wrong and writes the corrected answer beside the call, joined
  by the state hash. That pair — what the rule said, what the model said, what a
  person decided — is the labelled example a local model is trained and reviewed
  against.
- **Retention never changes an outcome.** Every write is best effort; a failure is
  logged and swallowed, exactly like a recorder failure.
- **Samples are grouped by redaction rules.** Each sample carries
  `redactor_version`, the `DecisionStateRedactor.Version` that produced its
  state. Redacted text cannot be re-redacted, because the original is never
  kept, so samples redacted under different rules must never train together.
  Only samples under the running version count towards
  `minimumSamplesPerDecision`; the report lists the other cohorts beside them,
  and a line with no stamp is its own cohort, version 0. Whoever changes a
  redaction rule bumps `Version` in the same commit.
- **Sample files are plain UTF-8 JSON lines**, with no byte-order mark, so a
  strict JSON-lines reader accepts every line.
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
switch: `Off` stops every decision point and every captain tool; each entry in `decisions` has its own `mode` and `gateThreshold`, and
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
into a JSON `items` array and each question names its 0-based path (`items[0]`,
`items[1]`, …), so the model answers one item at a time and code splits the
answers. A request holds at most 100 questions whose combined state stays within
`maxStateChars` and whose state plus question text stays within 80,000
characters, and each item is still gated and recorded on its own event
(with an even share of the request's tokens). `papercut_merge` still asks per
pair, because each comparison depends on the merges before it.

A list decision asks one question per listed item and names that item by its
JSON path. Empty slots are not sent; code owns the tally. Compound judgments
are split into one literal question each and combined in code (`test_covers`
splits fail-before from pass-after; `refusal` splits a quoted phrase from the
captain declining; `change_substance` splits safety-step, auth-guard, and wire-byte
risk). Noul instructions state the condition in the positive, and a negated
reading (an unmet criterion) is inverted in code. Counts and durations go to
the model as named buckets (`owner_digest` `chain_size` / `wait_age`), not as
raw numbers to compare.

A synthetic evaluation set checks the decisions that gate the recovery, review,
and Linter seams (`failure_cause`, `refusal`, `runtime_failure`,
`review_substance`, `lint_finding`). Each case builds its requests through the
decision's own adapter, so it tests the exact state and questions production
sends. A reference case pairs two variants that differ in one relevant fact and
states the answer for each; a consistency case changes a fact that must not
matter and requires the answers to agree. Operators run it with
`armada_typed_decision_eval`. The tool remains registered when no provider key
is available; its cases then report unavailable. The switchable client forwards
model observations after a key is added or replaced. Evaluation also runs in the background whenever the
provider reports a model version that has not been evaluated
(`typedDecisions.evalOnModelChange`, default `true`). Each run records one
`typed_decision.eval` event with the model and each case's outcome. A failing
case is a finding to review — a threshold, a question, or the model — not a
build failure.

One decision point runs behind the deterministic dock-boundary scanner:

- `leak_hunk` (D7, ships `Gate` at `0.90`) is an ADVISORY per-hunk leak
  classifier that runs BEHIND the deterministic dock-boundary scanner, at all
  three of its gates: the merge-queue integration scan, the pre-land mission
  scan, and the landing handler's gate. The scanner runs
  first and unconditionally and decides the block on its own; this pass then reads
  the added hunks of the same diff and asks one Noul per leak class
  (`kind_operator_note`, `kind_customer_detail`, `kind_orchestration_id`,
  `kind_host_or_path`) plus an advisory Choice (`leak_kind`: `operator_note`,
  `customer_detail`, `orchestration_id`, `host_or_path`, `none`). Code takes the
  greatest class Noul as the leak probability. At or above the threshold it
  attaches a `leak_hunk_flag` advisory flag naming the file and the suspected
  class. The flag never blocks: it never sets a scan result to failed, never
  transitions a merge entry or a mission to failure, and no answer at any
  confidence demotes, clears, or softens a deterministic finding — a clean
  deterministic scan carrying flags still lands, and the flag asks a person to
  look. The question states the domain: the work is authorized engineering on
  owned systems, where authentication, access-control, handshake and
  cryptographic code is ordinary engineering and never a leak, and where an
  ordinal, index or corpus id is a pointer that is safe to commit while only the
  value it resolves to is a secret. Its state is three fields — the vessel's
  display name, the repository-relative file path, and the bounded added hunk —
  redacted before egress, and the event carries the state hash and byte count,
  never the hunk. Call volume is bounded per file and per scan, and a timeout, a
  non-2xx, a 429, a 529 or a parse error leaves the deterministic verdict
  standing with an `unavailable` event.

**`log_watch`** (ships `Gate`, threshold `0.90`) is the model pass of the
read-only captain-log screen, beside the deterministic pass. Over the bounded
tail the screen already read, it asks one Choice `off_course` (`on_track`,
`wrong_premise`, `wrong_base`, `misread_stage`, `blocked_unstated`, `unclear`)
and one Noul `correctable_now`. A tail whose final outcome is already
`[ARMADA:RESULT] BLOCKED` is not read: the captain stated its block, and the
completion path raises the question to the owner. At or above the threshold with a class other
than `on_track`, it returns one finding, so the screen posts its single
voyage-tagged board note naming the drift class with one line of evidence, and
the decision emits one `captain.course_flag` event carrying the mission, the
voyage, the class and the reading. That event is deliberately distinct from the
typed-decision bookkeeping events, so an operator query for course flags is
clean. Off, below threshold, or unavailable reports nothing and records the
decision's own shadow or unavailable event. The screen never stops a captain:
its only writes are the note and the events, and the deterministic stall and
overdue rules are untouched. The questions state the domain — the captains do
authorized engineering on owned systems, so authentication and access-control
work in a log is ordinary engineering and never a drift.

The typed-decision system is also offered to captains directly, as MCP tools next to
the memory tools. `armada_typed_decision` is the general form: the captain supplies its
own state and questions. `armada_score_items` is the list form: the captain supplies
real items and a claim, the tool asks one Noul per item named at `items[i]`, and code
returns each noul plus the expected count (the sum). Do not ask Jev to count, ignore
siblings, or pad empty slots. The rest are pre-shaped, and each follows its own decision:
`armada_check_premise` (`premise_check`) checks a captain's own reading of the task
before it starts; `armada_memory_triage` (`memory_record`) triages a memory candidate
before the Recorder writes it; `armada_check_prior_art` (`prior_art`) checks whether the
work already exists before the captain writes a type — the D26 retrieval plus typed
answers for its stated plan; `armada_change_quality` (`change_quality`) returns a
per-dimension read of a focused diff before the Judge sees it; and
`armada_corpus_prelabel` (`corpus_prelabel`) returns the provisional kind of a captured
decision for the operator's corpus drafter. `armada_run_custom_decision`
runs a user-defined custom decision by name (see "Custom decisions" below). Every tool
above is caller-scoped, so a mission caller reaches it like the memory tools; defining a
custom decision stays a settings write, which only an administrator makes. Authority does
not travel with any of them. Each call redacts its state
before egress (there is no per-mission call cap) and has no side effect on any Armada
record: it dispatches nothing, lands nothing, edits no objective, and writes no memory.
Every call that carries its required arguments writes exactly one event, whatever the
outcome. The general tool and the pre-shaped helpers write `typed_decision.captain`. The
custom runner writes `typed_decision.captain` when the call never reaches the provider
(the tool is disabled, typed decisions are off, or the decision is unknown or `Off`) and otherwise records through
the custom decision's own path (see "Custom decisions" below). Every event carries only
the state hash and byte count.

The captain tools gate nothing, so they apply no threshold. Every answer comes back with
its confidence (and its probabilities where the provider gives them), so the captain
weighs a low-confidence answer itself. A pre-shaped helper follows its decision's mode:
`Off` makes no call and returns `unavailable` (reason `disabled`); **`Shadow` consults the
provider and records the answer (`gate_outcome` `shadow`), but returns `unavailable` with
reason `shadow`**, so the captain decides alone; `Gate` returns the answers. Shadow is the
demotion target for a decision whose answers mislead, so it quiets the helper as well as
recording it. The general tool has no decision of its own; it answers while the tool is
enabled and the global mode is not `Off`. A pre-shaped helper records under its decision's own key (for example
`premise_check`), so that decision's `retainState` also retains the helper's calls, with
rule verdict `none`. The general tool records under `captain_tool`. It ships with no
settings entry, so it is retained only when an operator adds `decisions.captain_tool`
with `retainState: true`; its mode there has no effect, because the general tool follows
`captainTool.enabled` and the global mode only. Its state and questions are whatever the captain chose, so
its samples are a general corpus rather than one decision's training set.
The global `mode` is the kill switch for the captain tools too. While the effective global
mode is `Off`, every tool (the general tool, the list tool, the pre-shaped helpers and the
custom runner) makes no provider call, records one `typed_decision.captain` event, and
returns `unavailable` with reason `typed_decisions_off`, or `typed_decisions_no_key` when
the global mode is on but no key resolves. The tools are enabled by default
(`typedDecisions.captainTool.enabled` is `true`); setting it `false` also makes every call
return `unavailable`, whatever the global mode. See `docs/MCP_API.md` for the tool
arguments.
The client validates each response against the questions sent. Missing answers, wrong
answer types, unknown Choice labels, invalid numeric ranges, or missing Choice/Score
confidence and probabilities return `unavailable` with reason `response_validation`.
No partial answers reach an adapter or a list tally. Noul confidence remains optional.
Rounded probabilities need not sum to exactly one. This check adds no provider call.

Tool events name the calling tool in `tool_name` and retain the supplied session identity
as `participant_key`, including calls
without a mission. This field is attribution only. It does not grant access and does
not fill `CaptainId`; that field comes from the mission for tool calls. Custom tool
calls use the same distinction, including their shadow and unavailable events.

Each wired decision holds an adapter over the shared client, never the raw
client, and every adapter follows one skeleton: Off returns the rule with no
call; unavailable returns the rule; Shadow or below threshold returns the rule
and records a shadow event; at or above threshold it combines the rule and the
model, where a rule hard-block always wins. Which decisions hold one is asserted
by the `Typed Decision Wiring` suite, never counted here. The decisions,
described in turn:

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
  decision `Off` the deterministic handoff stands. A stage whose final result is a
  stated `[ARMADA:RESULT] BLOCKED` never reaches this decision: the completion
  path fails it with its question first, whatever the decision's mode (see
  [PERSONAS.md](PERSONAS.md)).

Three persona-specific decision points sit on Judge and handoff seams (all ship
`Gate`):

- `revision_kind` (D21) sits after `ParseJudgeVerdict` on a NEEDS_REVISION's
  revision items, before autonomous recovery classifies the failure. The model
  answers a `kind` Choice {behaviour, test, comment_only, doc_only, boundary} per
  listed item. Code combines those answers: when every listed item is
  non-behavioural at or above threshold, the seam marks the failure
  `revision_comment_only`, so autonomous recovery **holds the rescue** (a
  comment-only NEEDS_REVISION is an operator landing, not a rescue chain), and
  opens an incident tagged for operator landing. A single behavioural or test
  item leaves the rule standing and the rescue proceeds. The model never lands;
  the finished work stays on its branch for the operator.
- `test_covers` (D22) sits on the TestEngineer handoff, over the added test
  methods and the objective's symptom sentence. The model answers `covers_symptom`,
  `asserts_source_text`, `fails_without_change`, and `passes_with_change` Nouls per added test; a
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
  The plain grouping is the input exactly as given: merges are applied to copies,
  so an unavailable answer after an earlier merge in the same listing discards
  that merge and every group keeps its original count.
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
  that mission wrote (at most 10) and asks the memory-review questions for each:
  `type_fits` and `durable_record` (combined in code as type-ok), `duplicate_of`
  (a choice among at most five existing records of the same vessel and type or
  topic) plus one `repeats_i` Noul per `existing_records[i]`, `will_go_stale`,
  and `belongs_in_ai_memory`.
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
  `scripts/autonomy/draft-corpus-line.mjs`, run outside the admiral and never in
  the dispatch loop. It drafts one line of the operator's decision corpus from an
  incident, a mission failure reason, a Mail signal, or a preflight result, so
  the operator does not start the capture rule from a blank line. It asks the
  classifier exactly one closed question, the **provisional kind** of the
  captured record, and fills nothing else the corpus rule does not already
  settle (a preflight line's `preventable_in_brief` follows from its failed
  questions). It never pre-fills the decision: `decided`, `decided_by` and
  `basis` are always left for the person.

  Its single hard guarantee is that every line it emits carries `"draft": true`
  — on the model path, on the fail-closed path, and when the model answers at
  full confidence. The operator confirms a draft by removing that flag and the
  `draft_meta` block beside it; the script never removes either and never writes
  a confirmed line.

  **How it reaches the classifier.** The script holds no provider key and never
  calls a provider. It calls the pre-shaped MCP tool `armada_corpus_prelabel` on
  the admiral (`docs/MCP_API.md`), which owns the key, shapes the one choice
  question over the corpus kinds, redacts again on its side, and records one
  event **under the `corpus_prelabel` decision** with the state's hash and byte
  count. That tool follows this decision's own mode, so the decision is scored
  per-decision and a post-gate review has calls to read. The script
  authenticates with the operator's own `ARMADA_API_KEY`, so the provider key
  stays in the admiral's environment variable or its protected key file. The
  script also holds no copy of the decision's mode or threshold: it reads both
  from `GET /api/v1/typed-decisions` per run, so this shipped decision governs
  the helper and a settings change takes effect with no edit here. The corpus
  kinds exist in two places in this repository — the tool's choices and the
  script's list — and a unit test compares them against each other, so a kind
  added to one and not the other fails rather than drifting.

  **Fail closed.** The decision `Off`, the global mode `Off`, a missing key
  (`typed_decisions_no_key`), `Shadow` mode (no call is made at all, since a
  shadowed reading may not be acted on), an unreachable admiral, a timeout, a
  non-2xx reply, a rate-limited or overloaded provider, an unparsable answer, a
  choice outside the corpus kinds, or a confidence below the gate threshold all
  produce a draft with **no** kind and the reason stated in
  `draft_meta.kind_reason`. A kind is never guessed from the input shape, and no
  failure reaches the operator as an unhandled error. State is redacted before it
  leaves the script, in the same order as the admiral's own guard, and only its
  hash and byte count are recorded on the draft.

  Run `node scripts/autonomy/draft-corpus-line.mjs --input <file.json>` (or pipe
  the input object on stdin), with `--out decisions.jsonl` to append the draft,
  `--kind <kind>` to set the kind yourself, or `--no-model` to draft without any
  classifier call. `node scripts/autonomy/test-draft-corpus-line.mjs` is its
  self-check; set `ARMADA_CORPUS_INGESTER` to the operator eval store's corpus
  ingester to also compare the corpus-kind mapping against the second copy that
  lives there, which is otherwise reported as skipped, never as passed.
Two platform-side decisions ship `Gate`:

- **`flake_score`** runs in `DefinitionOfDoneGate` after
  `DefinitionOfDoneFailureClassifier` classifies a failed unit-test command. It
  asks a `flake_likelihood` Score `[deterministic, likely real, likely load,
  known flaky family]`, an `outside_diff` Noul, and a `passes_in_isolation` Noul
  over state carrying the failing
  test names, the assertion lines, the touched files, whether the same tests
  failed on another branch in the last 24 hours, and the classifier's class. In
  `Gate`, a `likely load` or `known flaky family` reading at or above threshold,
  or a high `passes_in_isolation` Noul at or above threshold,
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
  for whether a human needs to act now. In `Gate` above the threshold each item
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
  home LINKS to an existing open objective (one `same_as_i` noul per
  `open_objectives[i]`; code takes the greatest) instead of creating one. A blocking item is only flagged
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

## Built-in decisions: a declarative definition and a C# rule

A built-in decision has two halves. Its **declarative** half is data: the questions and their
option or level meanings, which fields of the decision's input are serialized into the state,
and the direction that reads a finding. This half is a **definition** — the same shape a custom
decision uses — shipped as an embedded default in source and overridable in settings under
`typedDecisions.decisions.<name>`. The engine that turns a definition into questions and state,
and reads the gate value from the answers, is the one the custom decisions use, so a built-in
and a custom decision are asked the same way. The decision's threshold and mode are the
`gateThreshold` and `mode` on its `typedDecisions.decisions.<name>` row.

Its **behavioural** half is C#, never data:

- **The deterministic rule** stays authoritative and is the fallback in every non-gated case. A
  definition never produces a verdict.
- **`Combine`** maps the rule verdict and the model reading to the effective verdict. A rule
  hard-block always wins, and `Combine` returns the rule or a **more conservative** verdict,
  never an approval. It is C#-only; a definition cannot express it.
- **`Interpret` and side effects** — how a decision reads its own answers into the single gate
  confidence, and any informative side signal such as the `refusal` blocked-on-premise papercut
  — stay in code.

### An owner-edited definition can never approve — enforced in code

A definition carries questions, wording, and a finding direction; it carries no action and no
verdict, because the verdict is `Combine`, which is C#-only and conservative at every confidence.
An operator override is merged onto the embedded default through a **field whitelist**, in code:

- **Overridable:** the human-facing wording (a question's instructions, the meanings of its
  existing options, a Noul's true and false pole meanings), the `gateThreshold`, and the `mode`.
- **Fixed by the embedded default, never taken from settings:** the set of question ids, each
  question's kind, the finding direction (a Choice's `flagOptions`, a Noul's true pole as the
  finding), and the `stateFields` whitelist.

The override type carries only the whitelisted wording fields, so a settings edit has no field
in which to add or remove a question, change a question's kind, change what an answer means for
the gate, or widen what the decision serializes. The merge applies a wording override by
matching an existing question id; an override for an unknown id is ignored, so the question set
cannot grow. A threshold moves only **when** the conservative action fires — a higher threshold
fires it less often, never the reverse — and the mode is capped by the global kill switch. The
`Built-in Decision Definition` suite drives an override that names an unknown question and each
forbidden change and asserts the effective definition's structure is unchanged, and drives a
wording override and asserts only the wording changed.

### `refusal`

`refusal` is defined this way. Its embedded default carries the `outcome` Choice with its five
options and their meanings, the `quotes_refusal_phrase` and `captain_declining` Nouls with their poles, and the `stateFields`
`mission_title`, `agent_output_tail`, and `marker_present`; its threshold and mode are the
`refusal` row (`Gate`, `0.90`). The adapter holds the behavioural half: the deterministic rule
(the structured `[ARMADA:RESULT] REFUSED` marker and the provider safeguard block are
authoritative), `Interpret` (the action confidence is the `refused_policy` choice confidence on
a promote, or the quoted-not-own combination on a demote), `Combine` (promote a prose policy refusal the rule
missed, demote a quoted phrase only at very high confidence, never overturn a hard-block), and
the blocked-on-premise papercut side effect. An operator rewords the `outcome` options or moves
the `refusal` threshold from settings without a deploy, and cannot change which outcomes are a
refusal.

The other built-in decisions carry their declaration in C# and adopt a definition one at a time.
The current per-decision status is the `TypedDecisionWiring` list and the eval store, not this
page.

## Custom decisions — operators define their own

Beyond the shipped decisions, an operator can define **custom typed decisions** from the
dashboard (**Settings > Typed decisions > Custom decisions**) or the settings API. A custom
decision carries its own questions and describes how to build its state, and it is stored in
`typedDecisions.custom` and hot-reloads in place like the built-in decisions.

A custom decision is **advisory by construction** and cannot break the safety contract:

- It runs at its `surface`. **`MissionDiff`** runs when a **Worker stage hands off** to a
  later stage (the same seam as `prior_art` and `test_covers`): every MissionDiff decision
  that is not `Off` reads the finished Worker's diff, in name order. A Worker with no later
  stage, a mission with no diff, and every other persona run nothing. An optional `vessels`
  list (names or ids, case-insensitive) scopes a MissionDiff decision to those vessels, so a
  question about one kind of repository is never asked of, and never sends, another's diff;
  empty means every vessel. The route refuses `vessels` on the CaptainTool surface. **`CaptainTool`** runs
  only when called through `armada_run_custom_decision`, which can also run a MissionDiff
  decision by name with a caller-supplied `context`. It never wires itself into recovery or
  landing.
- On the MissionDiff surface the state is built from the finished mission: `title`,
  `persona`, `diff`, `output_tail` (the last 4096 characters of the agent output),
  `changed_paths` (one per line), and `failure_reason`; `stateFields` picks which, in order
  (empty means `diff` and `output_tail`).
- Its `binding` is one of a fixed, non-approving list (`CustomDecisionSeamEnum`): today `None`
  (record only, never flags) or `MissionDiffFlag`. When a `MissionDiffFlag` decision gates,
  the call is recorded as a `typed_decision.gated` event and, at the Worker handoff, **one
  Judge review instruction per flagged decision is prepended to the next brief** inside an
  `[ORCHESTRATOR NOTES]` block that names the "custom decision check", the decision, the
  flagged questions, and the gate value. The note says plainly that a flag is a review
  instruction, not a verdict: the Judge still judges. Through the tool, the reply says
  `flagged: true` and names `flaggedQuestions`. No member lands, dispatches, approves a PASS,
  holds a rescue, fails a stage, or writes memory. Adding an approving action would change
  the owner non-negotiables and needs a new ruling.
- Its effective mode is the minimum of the global mode and its own, so the global kill switch
  caps it. It is created `Off`, so nothing runs until an operator turns it on.
- It gates only at or above its `gateThreshold`, and only its bound conservative action; below
  the threshold, in Shadow, or unbound, it records and does nothing else. Through the tool,
  `Gate` returns the answers (below the threshold too; only `flagged` depends on the gate)
  and **`Shadow` records the answer but returns `unavailable` with reason `shadow`**, the
  same as a pre-shaped helper.
- **The gate value is the highest raw Noul probability**, the same reading every built-in
  decision uses: a Noul is the probability that its statement is true, so phrase each Noul
  with the finding as its **true** pole ("the change duplicates existing logic", not "the
  change reuses existing logic"). A Noul phrased the other way flags the good case. A
  **Choice** gates only when it names `flagOptions` (the options that are the finding) and
  the model picks one of them, at the confidence it gave that option; the route refuses a
  finding option that is not an option, and a list naming every option. A Choice without
  finding options, and every Score, is recorded and returned but never gates.

Each custom decision defines `questions` (choice, score, or noul, exactly like the built-ins),
`stateFields` (which context fields go into the state, in order), a `description`, and
`retainState`. A call that consults the provider records one event through the shared
recorder under the decision point `custom:<name>`: `typed_decision.gated` when it flags,
`typed_decision.shadow` (`gate_outcome` `shadow_mode` or `below_threshold`) when it only
records, and `typed_decision.unavailable` when the provider does not answer. A tool call
that never reaches the provider records one `typed_decision.captain` event under the same
decision point, with `gate_outcome` `disabled` (the captain tool is off),
`typed_decisions_off` or `typed_decisions_no_key` (the global kill switch), `not_found` (no
such decision), or `dormant` (the decision is `Off`). The handoff skips an `Off` decision
without an event, like every built-in seam. Events carry the state hash and byte count,
never the state. Read them with:

```sql
select payload from events where payload like '%"decision":"custom:%';
```

`retainState` works as for a built-in decision: with `typedDecisions.retention.enabled` on,
the call's redacted state is kept under `typed-decision-samples/custom_<name>/`, and
`armada_typed_decision_labels` lists the decision as `custom:<name>`.

Administrator routes (same permission as a settings write):

| Route | Effect |
| --- | --- |
| `PUT /api/v1/typed-decisions/custom/{name}` | Create or replace a custom decision. Refuses a name that collides with a shipped decision, an invalid mode/threshold/surface/binding, a `MissionDiffFlag` binding without the `MissionDiff` surface, or a malformed question, with 400. |
| `DELETE /api/v1/typed-decisions/custom/{name}` | Remove a custom decision. Deleting one does not resurrect it from the seeds. |
| `POST /api/v1/typed-decisions/custom/install-seeds` | Install the built-in generic example custom decisions that are not already present. They ship `Off`. |

`GET /api/v1/typed-decisions` returns every custom decision under `custom`.

The example seeds are generic software-engineering checks, meant only to show the shape of a
custom decision. A deployment defines its own decisions in the dashboard or imports them as
configuration; domain-specific decisions are never shipped in this source.
