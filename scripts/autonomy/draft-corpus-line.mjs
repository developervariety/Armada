#!/usr/bin/env node
// D14 corpus_prelabel -- operator-side helper. It drafts one decision-corpus line
// (the schema in the decision-corpus README) from an incident, a mission failure
// reason, a Mail signal, or a preflight result, so the operator does not start the
// capture rule from a blank line.
//
// It runs OUTSIDE the admiral: it reads one input object and emits one draft line.
// The single hard guarantee is that every line it emits carries "draft": true and
// nothing it emits is a confirmed line. The operator confirms a draft by removing
// the "draft" flag and its "draft_meta"; this script never removes them and never
// writes a confirmed line. It also never pre-fills the decision: "decided",
// "decided_by" and "basis" are always left empty for the person.
//
// WHERE THE CLASSIFIER COMES IN
//
// One closed question is asked of the typed-decision system: the PROVISIONAL KIND of
// the captured decision. The script holds no provider key and never talks to a
// provider. It calls the Armada MCP tool `armada_corpus_prelabel` on the admiral, which
// owns the key, shapes the question itself, redacts again on its side, and records one
// event under the `corpus_prelabel` decision carrying the state's hash and byte count.
// The script authenticates to Armada with the operator's own Armada API key, so the
// provider key stays in the admiral's environment variable or its protected key file
// and never reaches an operator-side script.
//
// The shipped decision governs the helper. The script reads the decision's mode and
// gate threshold from the admiral rather than holding its own copy: the decision Off,
// the global mode Off, a missing key, or Shadow mode all mean no proposed kind.
//
// FAIL CLOSED
//
// No key, an unreachable admiral, a timeout, a non-2xx reply, a rate-limited or
// overloaded provider, an unparsable answer, an answer below the gate threshold, or a
// choice outside the corpus kinds all yield a draft with kind null and a stated
// reason in draft_meta.kind_reason. A kind is never guessed, and no failure reaches
// the operator as an unhandled error.
//
// Configuration (all optional except the API key when a model answer is wanted):
//   ARMADA_API_URL      admiral REST base URL (default http://127.0.0.1:7890)
//   ARMADA_MCP_URL      admiral MCP endpoint  (default http://127.0.0.1:7891/mcp)
//   ARMADA_API_KEY      operator Armada API key; without it the script runs offline
//   ARMADA_TYPED_DECISION_TIMEOUT_MS  per-request timeout (default 20000)
//
// Usage:
//   node draft-corpus-line.mjs --input <file.json>        # read the input object from a file
//   cat input.json | node draft-corpus-line.mjs           # or from stdin
//   node draft-corpus-line.mjs --input i.json --out decisions.jsonl   # also append the line
//   node draft-corpus-line.mjs --input i.json --kind refusal          # the operator sets the kind
//   node draft-corpus-line.mjs --input i.json --no-model              # never call the classifier
//
// Input object (any one shape; unknown fields are ignored):
//   { "type": "incident",        "incident_id", "objective_id", "mission_id", "vessel", "title", "summary", "root_cause" }
//   { "type": "mission_failure", "mission_id", "objective_id", "vessel", "failure_reason" }
//   { "type": "mail",            "mission_id", "vessel", "payload" }
//   { "type": "preflight",       "objective_id", "vessel", "title", "failed_questions": [..], "platform_said", "disposition" }

import { readFileSync, appendFileSync } from "node:fs";
import { createHash } from "node:crypto";

// The flag whose presence marks a line as an unconfirmed draft. Removing it is the
// operator's confirm step; this script only ever sets it.
export const DRAFT_FLAG = "draft";

/** The shipped typed-decision key this helper belongs to. */
export const DECISION_KEY = "corpus_prelabel";

/** The question id the classifier answers. */
export const KIND_QUESTION_ID = "provisional_kind";

/** Where a drafted kind came from. An unresolved kind always carries a reason. */
export const KIND_SOURCE = Object.freeze({
  operator: "operator",
  model: "model",
  unavailable: "unavailable",
});

// The corpus kinds. The admiral's pre-shaped helper offers the same vocabulary to the
// classifier, and a test in that repository compares the two lists against each other,
// so a kind added on one side and not the other fails instead of drifting.
/** The corpus kinds, with the meaning each one carries for the classifier. */
export const KIND_MEANINGS = Object.freeze({
  preflight: "A dispatch preflight result: the objective was checked before dispatch, and the line records whether it was dispatched or held.",
  blocked_question: "A question the worker could not answer alone, which an owner or operator had to rule on.",
  operator_mail: "A message an operator sent into a running piece of work, and what it changed.",
  failure_class: "A failed piece of work whose cause had to be classified (infrastructure, provider, test, or the change itself).",
  refusal: "A runtime declined to do the work, and a person decided how to route it.",
  rescue_or_land: "A choice between rescuing a partly finished piece of work and landing what exists.",
  brief_defect: "A defect in the instructions themselves, found after the work started.",
});

/** The corpus kinds, in schema order. */
export const CORPUS_KINDS = Object.freeze(Object.keys(KIND_MEANINGS));

// A corpus kind maps to the decision that is scored against it. The eval-store
// ingester holds the same mapping for the same kinds; the two are compared by the
// self-check so they cannot drift apart. A kind that no scored decision answers maps
// to null, which is a value with its own meaning and never a missing entry: those
// kinds record an owner ruling, not a classifier call.
export const KIND_TO_DECISION = Object.freeze({
  preflight: "preflight",
  blocked_question: null,
  operator_mail: "inbox_triage",
  failure_class: "failure_cause",
  refusal: "refusal",
  rescue_or_land: "failure_cause",
  brief_defect: "preflight",
});

/** Why a kind names no scored decision. */
export const UNSCORED_KIND_REASON = "unscored_kind: this kind records an owner ruling, not a scored decision";

const DEFAULT_API_URL = "http://127.0.0.1:7890";
const DEFAULT_MCP_URL = "http://127.0.0.1:7891/mcp";
const DEFAULT_TIMEOUT_MS = 20000;
const TYPED_DECISION_TOOL = "armada_corpus_prelabel";

// The state budget for one call. The admiral applies its own, smaller or larger,
// budget after this one; this cap only keeps an operator-side payload bounded.
const MAX_STATE_CHARS = 8000;

// ------------------------------------------------------------------ redaction

// Redaction runs before the state leaves this process, in the same order as the
// admiral's own guard: ids before hashes, hosts before hashes, so a later pattern
// cannot eat a value an earlier one should have removed. The admiral redacts again;
// this pass means nothing private is in the payload even in transit to it.
const REDACTIONS = [
  [/\b(flt_|vsl_|cpt_|msn_|vyg_|obj_|inc_|chk_|mrg_|dock_|cmsg_|sig_|evt_)[A-Za-z0-9_]+/g, "#id"],
  [/(?:\/srv|\/home|\/tmp|\/Volumes|[A-Za-z]:\\)[^\s"',;<>]*/g, "<path>"],
  [/\b[a-zA-Z][a-zA-Z0-9+.\-]*:\/\/[^\s"',<>]+/g, "<host>"],
  [/\b(?:\d{1,3}\.){3}\d{1,3}\b/g, "<host>"],
  [/\b(?:[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?\.)+[A-Za-z]{2,}\b/g, "<host>"],
  [/\b[0-9a-fA-F]{7,40}\b/g, "#sha"],
  [/\bsk-[^\s"',<>]+|\bghp_[^\s"',<>]+|\bglpat-[^\s"',<>]+|\b[A-Za-z0-9+/]{32,}={0,2}/g, "<secret>"],
];

function redactText(text) {
  let working = typeof text === "string" ? text : "";
  for (const [pattern, replacement] of REDACTIONS) working = working.replace(pattern, replacement);
  return working;
}

/**
 * Redact a state object for egress and describe exactly what would leave.
 *
 * @param {object} state the state object; string values are redacted in place
 * @param {number} [maxChars] serialized character budget
 * @returns {{state: object, text: string, sha256: string, bytes: number}}
 */
export function redactForEgress(state, maxChars = MAX_STATE_CHARS) {
  const budget = maxChars > 0 ? maxChars : 1;
  const source = state && typeof state === "object" ? state : {};
  const clean = {};
  for (const [key, value] of Object.entries(source)) {
    if (value === null || value === undefined) continue;
    clean[redactText(key)] = typeof value === "string" ? redactText(value) : redactText(JSON.stringify(value));
  }

  let text = JSON.stringify(clean);
  if (text.length > budget) {
    // Trim the longest field until the payload fits, so the result stays valid JSON
    // instead of a truncated object the provider cannot read.
    const keys = Object.keys(clean).sort((a, b) => String(clean[b]).length - String(clean[a]).length);
    for (const key of keys) {
      if (text.length <= budget) break;
      const over = text.length - budget;
      const value = String(clean[key]);
      clean[key] = value.length > over ? value.slice(0, Math.max(0, value.length - over - 3)) + "..." : "...";
      text = JSON.stringify(clean);
    }
    if (text.length > budget) text = text.slice(0, budget);
  }

  return {
    state: clean,
    text,
    sha256: createHash("sha256").update(text, "utf8").digest("hex"),
    bytes: Buffer.byteLength(text, "utf8"),
  };
}

// --------------------------------------------------------------- state shaping

function firstSentences(text, count) {
  if (typeof text !== "string") return "";
  const trimmed = text.trim().replace(/\s+/g, " ");
  if (trimmed === "") return "";
  const parts = trimmed.split(/(?<=[.!?])\s+/).slice(0, count);
  return parts.join(" ");
}

function questionFor(input) {
  if (!input) return "";
  switch (input.type) {
    case "incident":
      return firstSentences(input.summary || input.title || input.root_cause || "", 2);
    case "mission_failure":
      return firstSentences(input.failure_reason || "", 2);
    case "mail":
      return firstSentences(input.payload || "", 2);
    case "preflight":
      return firstSentences(input.title || "", 2);
    default:
      return firstSentences(input.question || input.text || "", 2);
  }
}

function platformSaidFor(input) {
  if (!input) return null;
  if (typeof input.platform_said === "string" && input.platform_said.trim() !== "") return input.platform_said.trim();
  if (input.type === "incident" && typeof input.root_cause === "string" && input.root_cause.trim() !== "") return input.root_cause.trim();
  if (input.type === "mission_failure" && typeof input.failure_reason === "string" && input.failure_reason.trim() !== "") {
    return input.failure_reason.trim();
  }
  return null;
}

/**
 * The structured state the classifier reads. It is the captured record only: no
 * operator commentary, no proposed answer, nothing the model could read as a hint
 * about which kind the operator already believes.
 *
 * @param {object} input the captured record
 * @returns {object} the state object, before redaction
 */
export function buildDecisionState(input) {
  const src = input && typeof input === "object" ? input : {};
  return {
    input_type: typeof src.type === "string" ? src.type : "unknown",
    title: typeof src.title === "string" ? src.title : "",
    summary: typeof src.summary === "string" ? src.summary : "",
    root_cause: typeof src.root_cause === "string" ? src.root_cause : "",
    failure_reason: typeof src.failure_reason === "string" ? src.failure_reason : "",
    payload: typeof src.payload === "string" ? src.payload : "",
    platform_said: platformSaidFor(src) ?? "",
    disposition: typeof src.disposition === "string" ? src.disposition : "",
    failed_questions: Array.isArray(src.failed_questions) ? src.failed_questions.join(",") : "",
  };
}

// ----------------------------------------------------------------- transports

function environmentValue(name, fallback) {
  const value = process.env[name];
  return value && value.trim() !== "" ? value.trim() : fallback;
}

async function requestJson(url, init, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(url, { ...init, signal: controller.signal });
    const body = await response.text();
    if (!response.ok) throw new Error("armada replied HTTP " + response.status + (body ? ": " + body.slice(0, 200) : ""));
    return body;
  } catch (err) {
    // The reason travels with the failure. A caller that only saw "failed" would have
    // to guess between a closed port, a refused key, and a rate-limited provider.
    if (err && err.name === "AbortError") throw new Error("request timed out after " + timeoutMs + " ms");
    throw err;
  } finally {
    clearTimeout(timer);
  }
}

function requireApiKey() {
  const key = environmentValue("ARMADA_API_KEY", null);
  if (!key) throw new Error("ARMADA_API_KEY is not set, so the admiral cannot be reached");
  return key;
}

/**
 * Read the shipped decision's mode and gate threshold from the admiral. The admiral is
 * the authority on both; keeping a second copy in this script is how the two would
 * disagree.
 *
 * @param {number} [timeoutMs] request timeout
 * @returns {Promise<object>} the typed-decision status document
 */
export async function fetchDecisionStatus(timeoutMs = DEFAULT_TIMEOUT_MS) {
  const base = environmentValue("ARMADA_API_URL", DEFAULT_API_URL).replace(/\/+$/, "");
  const body = await requestJson(base + "/api/v1/typed-decisions", {
    method: "GET",
    headers: { "X-Api-Key": requireApiKey(), Accept: "application/json" },
  }, timeoutMs);
  return JSON.parse(body);
}

// One JSON-RPC exchange with the admiral's MCP endpoint. The transport is stateless,
// so each call carries its own credentials and needs no session handshake. A reply may
// arrive as JSON or as an event stream, so both are read.
async function mcpCall(method, params, timeoutMs) {
  const url = environmentValue("ARMADA_MCP_URL", DEFAULT_MCP_URL);
  const body = await requestJson(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "application/json, text/event-stream",
      "X-Api-Key": requireApiKey(),
    },
    body: JSON.stringify({ jsonrpc: "2.0", id: 1, method, params }),
  }, timeoutMs);

  let payload = body.trim();
  if (payload.startsWith("event:") || payload.startsWith("data:")) {
    const dataLines = payload.split(/\r?\n/).filter((line) => line.startsWith("data:")).map((line) => line.slice(5).trim());
    if (dataLines.length === 0) throw new Error("the admiral returned an event stream with no data frame");
    payload = dataLines[dataLines.length - 1];
  }

  const message = JSON.parse(payload);
  if (message.error) throw new Error("armada MCP error: " + (message.error.message || JSON.stringify(message.error)));
  return message.result;
}

/**
 * Ask the admiral's corpus pre-label helper for the provisional kind of an
 * already-redacted record. The helper shapes the question and records the event under
 * its own decision; the provider key lives on the admiral, and this call carries only
 * the operator's Armada API key.
 *
 * @param {object} record redacted record
 * @param {number} [timeoutMs] request timeout
 * @returns {Promise<object>} the tool's answer object
 */
export async function askArmada(record, timeoutMs = DEFAULT_TIMEOUT_MS) {
  const result = await mcpCall("tools/call", { name: TYPED_DECISION_TOOL, arguments: { record } }, timeoutMs);
  const text = result && Array.isArray(result.content)
    ? result.content.filter((part) => part && part.type === "text").map((part) => part.text).join("")
    : null;
  if (!text) throw new Error("the typed-decision tool returned no content");
  return JSON.parse(text);
}

// ------------------------------------------------------------- kind resolution

function unresolved(reason, extra = {}) {
  return { kind: null, source: KIND_SOURCE.unavailable, confidence: null, reason, stateSha256: null, stateBytes: 0, ...extra };
}

function statusField(source, ...names) {
  if (!source || typeof source !== "object") return undefined;
  for (const name of names) {
    if (Object.prototype.hasOwnProperty.call(source, name)) return source[name];
    const lower = name.charAt(0).toLowerCase() + name.slice(1);
    if (Object.prototype.hasOwnProperty.call(source, lower)) return source[lower];
    const upper = name.charAt(0).toUpperCase() + name.slice(1);
    if (Object.prototype.hasOwnProperty.call(source, upper)) return source[upper];
  }
  return undefined;
}

/**
 * Resolve the provisional kind for one captured record.
 *
 * Every return is either a kind the operator or the gated classifier supplied, or no
 * kind at all with a stated reason. A kind is never guessed from the input shape.
 *
 * @param {object} input the captured record
 * @param {object} [options] kindOverride, noModel, fetchStatus, askModel, timeoutMs
 * @returns {Promise<{kind: string|null, source: string, confidence: number|null, reason: string|null, stateSha256: string|null, stateBytes: number}>}
 */
export async function resolveProvisionalKind(input, options = {}) {
  const kindOverride = options.kindOverride ?? null;
  if (kindOverride !== null) {
    if (!CORPUS_KINDS.includes(kindOverride)) {
      return unresolved("kind_override_is_not_a_corpus_kind: " + kindOverride);
    }
    return { kind: kindOverride, source: KIND_SOURCE.operator, confidence: null, reason: null, stateSha256: null, stateBytes: 0 };
  }

  if (options.noModel === true) return unresolved("model_not_requested: the run asked for no classifier call");

  const timeoutMs = options.timeoutMs ?? Number(environmentValue("ARMADA_TYPED_DECISION_TIMEOUT_MS", String(DEFAULT_TIMEOUT_MS)));
  const fetchStatus = options.fetchStatus ?? (() => fetchDecisionStatus(timeoutMs));
  const askModel = options.askModel ?? ((record) => askArmada(record, timeoutMs));

  let status;
  try {
    status = await fetchStatus();
  } catch (err) {
    return unresolved("decision_status_unavailable: " + messageOf(err));
  }

  const effectiveMode = String(statusField(status, "effectiveMode") ?? "Off");
  if (effectiveMode !== "Gate" && effectiveMode !== "Shadow") {
    const why = statusField(status, "effectiveReason");
    return unresolved("typed_decisions_off: " + (why || "the global mode is " + effectiveMode));
  }

  const decisions = statusField(status, "decisions");
  const entry = Array.isArray(decisions)
    ? decisions.find((row) => String(statusField(row, "key") ?? "") === DECISION_KEY)
    : undefined;
  if (!entry) return unresolved("decision_not_in_settings: " + DECISION_KEY + " is unknown to the admiral");

  const decisionMode = String(statusField(entry, "mode") ?? "Off");
  if (decisionMode === "Off") return unresolved("decision_off: " + DECISION_KEY + " is set Off");
  if (decisionMode === "Shadow" || effectiveMode === "Shadow") {
    // A shadowed decision may not act, and a kind this helper cannot use is a kind not
    // worth sending state for. So the call is not made at all, and the draft says so
    // rather than implying a shadow reading exists somewhere to review.
    return unresolved("decision_shadow: the decision is shadowed, so no call was made and no kind is proposed");
  }

  const threshold = Number(statusField(entry, "threshold"));
  if (!Number.isFinite(threshold)) return unresolved("decision_threshold_unreadable: the admiral reported no gate threshold");

  const egress = redactForEgress(buildDecisionState(input));

  let answer;
  try {
    answer = await askModel(egress.state);
  } catch (err) {
    return unresolved("model_unavailable: " + messageOf(err), { stateSha256: egress.sha256, stateBytes: egress.bytes });
  }

  const withState = { stateSha256: egress.sha256, stateBytes: egress.bytes };
  if (!answer || answer.available !== true) {
    const reason = (answer && (answer.reason || answer.unavailableReason)) || "unavailable";
    return unresolved("model_unavailable: " + reason, withState);
  }

  const answers = answer.answers;
  const chosen = answers && typeof answers === "object" ? answers[KIND_QUESTION_ID] : null;
  if (!chosen || typeof chosen !== "object") {
    return unresolved("answer_parse_failed: the reply carried no " + KIND_QUESTION_ID + " answer", withState);
  }

  const choice = typeof chosen.choice === "string" ? chosen.choice : null;
  if (!choice) return unresolved("answer_parse_failed: the answer carried no choice", withState);
  if (!CORPUS_KINDS.includes(choice)) return unresolved("model_returned_unknown_kind: " + choice, withState);

  const confidence = typeof chosen.confidence === "number" ? chosen.confidence : null;
  if (confidence === null) return unresolved("answer_parse_failed: the answer carried no confidence", withState);
  if (confidence < threshold) {
    return unresolved("below_gate_threshold: " + confidence + " < " + threshold, withState);
  }

  return { kind: choice, source: KIND_SOURCE.model, confidence, reason: null, ...withState };
}

function messageOf(err) {
  if (!err) return "unknown error";
  if (typeof err === "string") return err;
  return err.message ? String(err.message) : String(err);
}

// ------------------------------------------------------------------- drafting

/**
 * Draft one decision-corpus line from an input object. The returned object is always
 * a DRAFT: it carries DRAFT_FLAG=true, and no code path removes it. Fields the corpus
 * rule cannot settle deterministically are left empty or null for the operator.
 *
 * @param {object} input the incident / mission_failure / mail / preflight object
 * @param {object} [kindOutcome] the result of resolveProvisionalKind; absent means unresolved
 * @param {() => Date} [now] clock, injected for tests
 * @returns {object} the draft corpus line
 */
export function draftCorpusLine(input, kindOutcome, now = () => new Date()) {
  const src = input && typeof input === "object" ? input : {};
  const outcome = kindOutcome && typeof kindOutcome === "object"
    ? kindOutcome
    : unresolved("kind_not_resolved: the drafter received no classifier outcome");

  const kind = typeof outcome.kind === "string" && CORPUS_KINDS.includes(outcome.kind) ? outcome.kind : null;
  const isPreflight = kind === "preflight";
  const failedQuestions = Array.isArray(src.failed_questions) ? src.failed_questions : [];

  const scoredDecision = kind === null ? null : KIND_TO_DECISION[kind] ?? null;
  let scoredReason = null;
  if (kind === null) scoredReason = "kind_unresolved: no kind, so no scored decision";
  else if (scoredDecision === null) scoredReason = UNSCORED_KIND_REASON;

  const line = {
    recorded_utc: now().toISOString().replace(/\.\d{3}Z$/, "Z"),
    // The classifier's provisional reading, or null when nothing resolved it. The
    // reason for a null kind is always stated in draft_meta.
    kind,
    vessel: typeof src.vessel === "string" ? src.vessel : null,
    objective_id: typeof src.objective_id === "string" ? src.objective_id : null,
    mission_id: typeof src.mission_id === "string" ? src.mission_id : null,
    incident_id: typeof src.incident_id === "string" ? src.incident_id : null,
    question: questionFor(src),
    // Options are the answers that were open; only the operator knows the one not taken.
    // A preflight line's options are the battery question numbers that answered "no".
    options: isPreflight ? failedQuestions : [],
    platform_said: platformSaidFor(src),
    // The operator fills the decision and its basis; the draft never decides.
    decided: "",
    // decided_by is a genuine judgement (owner / operator / deterministic); left null.
    decided_by: null,
    basis: "",
    // The one field the corpus rule settles deterministically: a preflight line is
    // preventable_in_brief exactly when it lists failed questions. A line whose kind
    // never resolved settles nothing, so it stays null.
    preventable_in_brief: isPreflight ? failedQuestions.length > 0 : null,
    outcome: "unknown",
    // The unconditional guarantee: this line is a draft. The operator confirms by
    // deleting this flag and draft_meta; the script never emits a line without them.
    [DRAFT_FLAG]: true,
    draft_meta: {
      decision: DECISION_KEY,
      kind_source: typeof outcome.source === "string" ? outcome.source : KIND_SOURCE.unavailable,
      kind_confidence: typeof outcome.confidence === "number" ? outcome.confidence : null,
      kind_reason: kind === null ? (outcome.reason || "kind_not_resolved") : null,
      scored_decision: scoredDecision,
      scored_decision_reason: scoredReason,
      // What left this process: its hash and its size, never the state.
      state_sha256: typeof outcome.stateSha256 === "string" ? outcome.stateSha256 : null,
      state_bytes: typeof outcome.stateBytes === "number" ? outcome.stateBytes : 0,
    },
  };

  return line;
}

/**
 * Read the eval-store ingester's KIND_MAP literal, so the two copies of one mapping
 * can be compared instead of trusted. Entries are returned in the drafter's order so a
 * comparison sees a difference in content, never in ordering.
 *
 * @param {string} source the ingester file's text
 * @returns {object} kind -> decision
 */
export function parseIngesterKindMap(source) {
  const text = typeof source === "string" ? source : "";
  const start = text.indexOf("KIND_MAP");
  if (start < 0) throw new Error("the ingester text holds no KIND_MAP");
  const open = text.indexOf("{", start);
  const close = text.indexOf("}", open);
  if (open < 0 || close < 0) throw new Error("the ingester's KIND_MAP is not a readable mapping literal");

  const parsed = {};
  const body = text.slice(open + 1, close);
  for (const raw of body.split("\n")) {
    const line = raw.trim();
    if (line === "" || line.startsWith("#")) continue;
    const match = line.match(/^["']([A-Za-z_]+)["']\s*:\s*["']([A-Za-z_]+)["']\s*,?/);
    if (match) parsed[match[1]] = match[2];
  }
  if (Object.keys(parsed).length === 0) throw new Error("the ingester's KIND_MAP held no entries");

  const ordered = {};
  for (const kind of CORPUS_KINDS) if (kind in parsed) ordered[kind] = parsed[kind];
  for (const kind of Object.keys(parsed)) if (!(kind in ordered)) ordered[kind] = parsed[kind];
  return ordered;
}

// ----------------------------------------------------------------------- main

function parseArgs(argv) {
  const args = { input: null, out: null, kind: null, noModel: false };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--input") args.input = argv[++i];
    else if (arg === "--out") args.out = argv[++i];
    else if (arg === "--kind") args.kind = argv[++i];
    else if (arg === "--no-model") args.noModel = true;
  }
  return args;
}

function readInput(inputPath) {
  const raw = inputPath ? readFileSync(inputPath, "utf8") : readFileSync(0, "utf8");
  if (!raw || raw.trim() === "") throw new Error("no input: provide --input <file> or pipe a JSON object on stdin");
  return JSON.parse(raw);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  let input;
  try {
    input = readInput(args.input);
  } catch (err) {
    process.stderr.write("draft-corpus-line: " + messageOf(err) + "\n");
    process.exit(1);
    return;
  }

  // Every failure inside the resolver is already a stated reason, so the operator sees
  // a draft and its reason instead of a stack trace.
  const outcome = await resolveProvisionalKind(input, { kindOverride: args.kind, noModel: args.noModel });
  const line = draftCorpusLine(input, outcome);

  // Never emit a line that is not a draft. This is a self-check on the guarantee, not
  // a reachable branch: if it ever fires the draft flag was dropped and we refuse to write.
  if (line[DRAFT_FLAG] !== true) {
    process.stderr.write("draft-corpus-line: refusing to emit a non-draft line\n");
    process.exit(2);
    return;
  }

  if (line.kind === null) {
    process.stderr.write("draft-corpus-line: no kind proposed (" + line.draft_meta.kind_reason + "); fill it in yourself\n");
  }

  const serialized = JSON.stringify(line);
  process.stdout.write(serialized + "\n");
  if (args.out) appendFileSync(args.out, serialized + "\n");
}

// Run only when invoked directly, so the test can import the functions without side effects.
if (import.meta.url === "file://" + process.argv[1]) {
  main().catch((err) => {
    process.stderr.write("draft-corpus-line: " + messageOf(err) + "\n");
    process.exit(1);
  });
}
