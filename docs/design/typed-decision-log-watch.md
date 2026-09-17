# Design — `log_watch`: running-captain log screening that posts a course flag (D8)

Status: design only. This document describes a decision point. It wires no code,
changes no test, and adds no migration. Implementation is a later row, opened
only after the owner reviews this design. Decision id: `log_watch`. Default
mode: **Gate** in the shipped settings default, with no effect until an adapter
is wired for this decision.

## Purpose

Voyage and mission events show only stage boundaries. A captain can work for an
hour on a wrong premise, the wrong base, or a misread stage, and that is visible
only in its running log. The owner directive is to watch running captain logs,
not only voyage state: catching a wrong reading in the log while the mission is
still `InProgress` saves the whole run, and a stage boundary is often the only
window in which a correction can still reach the next brief.

`log_watch` adds a **bounded, read-only screen** over each `InProgress`
mission's log on a cadence. When the log shows a captain working on a wrong
premise, a wrong base, or a misread stage, the screen posts a **voyage-tagged
board note** and emits a **`captain.course_flag`** event, so the operator sees
the drift early. It never touches the captain.

## Non-negotiables

1. **The screen never stops a captain.** It cannot cancel, pause, kill, restart,
   re-dispatch, Mail, or steer a mission. It reads the log and posts a note.
   Correcting or halting a mission stays an operator action.
2. **Read-only.** The screen reads the log tail through the existing shared-read
   path and writes nothing to any mission, voyage, captain, dock, or check
   record. Its only writes are one board note and one event.
3. **Nothing leaves unredacted.** The log tail passes through
   `DecisionStateRedactor` before egress. State is recorded as a SHA-256 and a
   byte count, never as text.
4. **The model informs; it never blocks or dispatches.** A flag is a note plus
   an event for the operator. The deterministic escalation rules (stall,
   overdue) are unchanged and independent.
5. **Gate by default.** The settings default is `Gate`; the decision only flags.

## Seams at the base tip

Line references are to the lane's integration base tip; re-verify after later landings.

### Periodic active-mission loop (the cadence host)

- `src/Armada.Core/Services/EscalationService.cs:117` — `EvaluateMissionOverdueAsync`
  already enumerates `EnumerateByStatusAsync(MissionStatusEnum.InProgress, ...)`
  on a cadence and executes an action per mission over a threshold. `log_watch`
  is a sibling evaluator of the same shape: enumerate `InProgress` missions,
  screen each one's log tail, act (post a note) on a flag. It reuses the
  existing cooldown map (`EscalationService._Cooldowns`, `:35`) so one mission is
  not flagged repeatedly for the same drift.
- `src/Armada.Core/Services/AdmiralService.cs:2253` — the admiral's own
  `EnumerateByStatusAsync(MissionStatusEnum.InProgress, ...)` in
  `RecoverOrphanedMissionsAsync`, the reference for iterating live missions
  inside the admiral loop.

### Log tail read (the read-only source)

- `src/Armada.Server/RemoteControlQueryService.cs:381` — `GetCaptainLogAsync`
  resolves the live log via the pointer file
  `{LogDirectory}/captains/{captainId}.current`, then reads lines through
  `ReadLinesSharedAsync` with a clamped line count (50 default, 1–1000). The
  screen reuses this shared-read shape to take a bounded tail of the running
  captain's log.
- `src/Armada.Server/RemoteControlQueryService.cs:324` — `GetMissionLogAsync`,
  the mission-scoped equivalent, when the flag should be keyed to the mission
  rather than the captain.

### Board note (voyage-tagged) and event

- `src/Armada.Server/CoordinationService.cs:121` — `PostMessageAsync`, the note
  writer; it also emits a Wake signal for an addressed note (`:204`).
- `src/Armada.Core/Models/CoordinationMessagePostRequest.cs:33` — `VoyageId`
  on the post request: this is what makes the note **voyage-tagged**. A
  voyage-tagged note is appended to that voyage's next stage brief, so the
  flag reaches the pipeline as well as the operator.
- `src/Armada.Core/Database/Interfaces/ICoordinationMessageMethods.cs:52` —
  `EnumerateByVoyageAsync`, so the screen can check whether it already posted a
  flag for this voyage before posting again.
- `src/Armada.Core/Services/AdmiralService.cs:2062` — `EmitEventAsync`, the
  event-writer pattern; `src/Armada.Core/Models/ArmadaEvent.cs:38/58/68` —
  `EventType` / `MissionId` / `VoyageId`. The `captain.course_flag` event is
  written this way, carrying the mission and voyage ids and owner scope.

### Typed-decision foundation (already landed, reused unchanged)

- `src/Armada.Core/Services/Interfaces/ITypedDecisionClient.cs:13` —
  `DecideAsync`; advisory, never throws.
- `src/Armada.Core/Services/DecisionStateRedactor.cs:69/94` — `Redact` /
  `RedactObject`.
- `src/Armada.Core/Models/TypedDecisions.cs:59` — `NoulQuestion`; `:41` —
  `ChoiceQuestion`.
- `src/Armada.Core/Settings/TypedDecisionSettings.cs:96` — `For`; `:122` —
  `["log_watch"] = ... Mode = Off` default.
- `src/Armada.Core/Services/TypedDecisionRecorder.cs:29/35/38` — the three
  event types.

## The seam for the screen

A new bounded evaluator, `LogWatchScreen`, runs on the `EscalationService`
cadence (or a dedicated timer of the same shape), guarded by
`settings.TypedDecisions.For("log_watch").Mode != Off`. Per tick:

1. Enumerate `InProgress` missions (the `EscalationService.EvaluateMissionOverdueAsync`
   pattern).
2. For each mission past a minimum age and not in cooldown for this screen, and
   only when the log has grown since the last screen of this mission, read a
   **bounded tail** of the running captain's log via the
   `RemoteControlQueryService` shared-read path.
3. Redact the tail and call the model once.
4. On a flag at or above threshold, post one voyage-tagged board note and emit
   one `captain.course_flag` event, then set the cooldown so the same drift is
   not re-flagged every tick.

The screen never reads more than the tail, never re-reads a mission whose log
has not grown, and never calls the model for a mission it has already flagged
within the cooldown window.

## State (one call per screened mission)

Egress class **H** (log tails), approved for redacted transmission. Per mission:

- The **log tail**, a bounded number of trailing lines (default informed by the
  four-week review, capped well under `TypedDecisions.MaxStateChars`), redacted
  through `DecisionStateRedactor`. The redactor preserves lines containing
  `[ARMADA:` markers, which carry the captain's own result and note lines.
- The mission's **stage persona** and **mission mode**.
- A short **brief anchor**: the objective title and the one-sentence deliverable,
  so the model can judge the log against what the stage was asked to do.
- The **base / start ref shape** the mission was given (as a redacted token, not
  a raw SHA), so a wrong-base reading is detectable.

Nothing else. No dock path, no host, no captain key in the state — the redactor
strips Armada ids, paths, hosts, hex, and key-shaped tokens. The recorder stores
`state_sha256` and `state_bytes`, never the tail.

Budget: one model call per screened mission per cooldown window; a call over the
settings timeout is unavailable, not late; the screen never blocks the admiral
loop waiting on the model.

## Questions

- `off_course` — `ChoiceQuestion` { on_track, wrong_premise, wrong_base,
  misread_stage, blocked_unstated, unclear }. Criteria phrase each drift class
  against the brief anchor: `wrong_premise` = the log assumes a fact the brief
  contradicts; `wrong_base` = the log is building on a different base or ref
  than the mission was given; `misread_stage` = the captain is doing another
  stage's work; `blocked_unstated` = the captain is stuck on missing context or
  an owner question but has not emitted `[ARMADA:RESULT] BLOCKED`.
- `correctable_now` — Noul. `TrueMeaning`: "a correction to the next stage's
  brief, or an operator note now, would change the outcome." `FalseMeaning`:
  "the drift is already past the point a note would help."

The instructions state the domain: authorized heavy-duty diagnostics;
seed-key / SecurityAccess work in a log is ordinary engineering, not a refusal
signal and not a drift.

## Gate — board note plus event only, never a stop

Effective mode is `min(global, decision)`.

- **Off** (default): no screening.
- **Shadow / below threshold**: `typed_decision.shadow` event with the tail's
  `state_sha256`, the `off_course` answer, and confidence. No board note. This
  is the data the four-week review reads to tune the threshold and the tail
  size.
- **Gate, at or above threshold** and `off_course != on_track`: post one
  **voyage-tagged board note** naming the drift class and the one line of
  evidence, emit one **`captain.course_flag`** event, and record a
  `typed_decision.gated` event. Set the mission's screen cooldown.

The board note is advisory to the captain and informative to the operator; a
voyage-tagged note is appended to the voyage's next stage brief, which is the
sanctioned handoff-time correction channel. The screen posts the flag; the
operator decides whether to Mail the next stage, cancel, or let the voyage run.
The screen never cancels, Mails, or re-dispatches.

## Events

- `typed_decision.shadow` — every below-threshold or Shadow-mode screen.
- `typed_decision.gated` — every at-or-above-threshold flag.
- `typed_decision.unavailable` — timeout, non-2xx, or parse failure; the screen
  posts nothing and tries again next tick.
- `captain.course_flag` — the operator-facing flag, carrying `MissionId`,
  `VoyageId`, the drift class, and the board-note id. Distinct from the
  typed-decision bookkeeping events so an operator query for course flags is
  clean.

## Tests it would need

Design-time only; the tests land with the implementation row.

- `LogWatchScreenTests` (table-driven, fake `ITypedDecisionClient`): Off → no
  read, no model call, no note; Shadow → shadow event, no note; unavailable →
  unavailable event, no note, retried next tick; Gate `on_track` → no note;
  Gate `wrong_premise` at threshold → exactly one voyage-tagged note and one
  `captain.course_flag` event; cooldown honoured so a second tick over the same
  unchanged tail posts nothing.
- A guard that the screen performs **no mission mutation**: assert the mission,
  voyage, and captain records are unchanged after a flag; only a coordination
  message and events are written.
- A guard that the note is **voyage-tagged**: the posted request carries
  `VoyageId`, and a second flag for the same voyage in the cooldown window does
  not double-post (checked via `EnumerateByVoyageAsync`).
- A redaction guard on the tail: a fixture tail with one instance of each
  redactor class produces state in which none survives, `[ARMADA:` lines are
  preserved, and a product identifier survives.
- A read-bound test: a mission whose log has not grown since the last screen
  issues no model call.

## Risk: a safety-tuned model misreads authorized diagnostics

Same domain risk as the leak classifier. A running log full of seed-key,
SecurityAccess, or challenge-response text can read to a safety-tuned model as a
refusal or a policy problem, producing a false `blocked_unstated` or
`wrong_premise` flag. Because the screen can only post a note, a false positive
costs an operator glance, not a stopped mission. The question instructions state
the domain, and the four-week review measures the flag's precision on the
diagnostic vessels first; a decision reversed by operators on more than 5% of
its gated outcomes drops back to Shadow until its criteria or threshold are
fixed.

## Not in scope

- No change to `EscalationService`'s deterministic stall and overdue rules.
- No authority to cancel, Mail, re-dispatch, or steer a mission.
- No captain-facing tool; this is an admiral-side read of the running log.
