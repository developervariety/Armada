#!/usr/bin/env node
// D14 corpus_prelabel — operator-side helper. It drafts one decision-corpus line
// (the schema in AI-Memory/corpus/README.md) from an incident, a mission failure
// reason, a Mail signal, or a preflight result, so the operator does not start the
// capture rule from a blank line.
//
// It runs OUTSIDE the admiral: it reads one input object and emits one draft line.
// The single hard guarantee is that every line it emits carries "draft": true and
// nothing it emits is a confirmed line. The operator confirms a draft by removing
// the "draft" flag; the script never removes it and never writes a confirmed line.
// The typed questions the corpus wants a label for (kind, preventable_in_brief,
// decided_by) are pre-filled deterministically only where the corpus rule already
// settles them (a preflight line's preventable_in_brief follows from its failed
// questions); every genuine judgement is left for the operator to fill on the row.
//
// Usage:
//   node draft-corpus-line.mjs --input <file.json>        # read the input object from a file
//   cat input.json | node draft-corpus-line.mjs           # or from stdin
//   node draft-corpus-line.mjs --input i.json --out decisions.jsonl   # also append the line
//   node draft-corpus-line.mjs --input i.json --kind refusal          # override the inferred kind
//
// Input object (any one shape; unknown fields are ignored):
//   { "type": "incident",        "incident_id", "objective_id", "mission_id", "vessel", "title", "summary", "root_cause" }
//   { "type": "mission_failure", "mission_id", "objective_id", "vessel", "failure_reason" }
//   { "type": "mail",            "mission_id", "vessel", "payload" }
//   { "type": "preflight",       "objective_id", "vessel", "title", "failed_questions": [..], "platform_said", "disposition" }

import { readFileSync, appendFileSync } from "node:fs";

// The flag whose presence marks a line as an unconfirmed draft. Removing it is the
// operator's confirm step; this script only ever sets it.
export const DRAFT_FLAG = "draft";

const CORPUS_KINDS = new Set([
  "preflight",
  "blocked_question",
  "operator_mail",
  "failure_class",
  "refusal",
  "rescue_or_land",
  "brief_defect",
]);

function firstSentences(text, count) {
  if (typeof text !== "string") return "";
  const trimmed = text.trim().replace(/\s+/g, " ");
  if (trimmed === "") return "";
  const parts = trimmed.split(/(?<=[.!?])\s+/).slice(0, count);
  return parts.join(" ");
}

function inferKind(input) {
  switch (input && input.type) {
    case "incident":
    case "mission_failure":
      return "failure_class";
    case "mail":
      return "operator_mail";
    case "preflight":
      return "preflight";
    default:
      return "blocked_question";
  }
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
 * Draft one decision-corpus line from an input object. The returned object is always
 * a DRAFT: it carries DRAFT_FLAG=true, and no code path removes it. Fields the corpus
 * rule cannot settle deterministically are left empty or null for the operator.
 *
 * @param {object} input the incident / mission_failure / mail / preflight object
 * @param {string} [kindOverride] force a corpus kind instead of the inferred one
 * @param {() => Date} [now] clock, injected for tests
 * @returns {object} the draft corpus line
 */
export function draftCorpusLine(input, kindOverride, now = () => new Date()) {
  const src = input && typeof input === "object" ? input : {};

  let kind = kindOverride && CORPUS_KINDS.has(kindOverride) ? kindOverride : inferKind(src);
  if (!CORPUS_KINDS.has(kind)) kind = "blocked_question";

  const isPreflight = kind === "preflight";
  const failedQuestions = Array.isArray(src.failed_questions) ? src.failed_questions : [];

  const line = {
    recorded_utc: now().toISOString().replace(/\.\d{3}Z$/, "Z"),
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
    // preventable_in_brief exactly when it lists failed questions. Otherwise null.
    preventable_in_brief: isPreflight ? failedQuestions.length > 0 : null,
    outcome: "unknown",
    // The unconditional guarantee: this line is a draft. The operator confirms by
    // deleting this flag; the script never emits a line without it.
    [DRAFT_FLAG]: true,
  };

  return line;
}

function parseArgs(argv) {
  const args = { input: null, out: null, kind: null };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--input") args.input = argv[++i];
    else if (arg === "--out") args.out = argv[++i];
    else if (arg === "--kind") args.kind = argv[++i];
  }
  return args;
}

function readInput(inputPath) {
  const raw = inputPath ? readFileSync(inputPath, "utf8") : readFileSync(0, "utf8");
  if (!raw || raw.trim() === "") throw new Error("no input: provide --input <file> or pipe a JSON object on stdin");
  return JSON.parse(raw);
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  let input;
  try {
    input = readInput(args.input);
  } catch (err) {
    process.stderr.write("draft-corpus-line: " + err.message + "\n");
    process.exit(1);
    return;
  }

  const line = draftCorpusLine(input, args.kind);

  // Never emit a line that is not a draft. This is a self-check on the guarantee, not
  // a reachable branch: if it ever fires the draft flag was dropped and we refuse to write.
  if (line[DRAFT_FLAG] !== true) {
    process.stderr.write("draft-corpus-line: refusing to emit a non-draft line\n");
    process.exit(2);
    return;
  }

  const serialized = JSON.stringify(line);
  process.stdout.write(serialized + "\n");
  if (args.out) appendFileSync(args.out, serialized + "\n");
}

// Run only when invoked directly, so the test can import the functions without side effects.
if (import.meta.url === "file://" + process.argv[1]) {
  main();
}
