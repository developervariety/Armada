#!/usr/bin/env node
// Self-check for draft-corpus-line.mjs (D14 corpus_prelabel).
//
// Run:  node scripts/autonomy/test-draft-corpus-line.mjs
// Exit: 0 when every check passes, 1 otherwise. No arguments, no network, no files
// written outside a throwaway temp path.
//
// The load-bearing guarantee it proves: every drafted line carries "draft": true and
// no input shape ever yields a confirmed line or a pre-decided answer.

import { draftCorpusLine, DRAFT_FLAG } from "./draft-corpus-line.mjs";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

let failures = 0;
function check(name, condition) {
  if (condition) {
    process.stdout.write("  PASS  " + name + "\n");
  } else {
    failures++;
    process.stdout.write("  FAIL  " + name + "\n");
  }
}

const fixedNow = () => new Date("2026-09-16T08:00:00Z");

const inputs = {
  incident: {
    type: "incident",
    incident_id: "inc_example",
    objective_id: "obj_example",
    mission_id: "msn_example",
    vessel: "ExampleVessel",
    title: "Gate failed on an out-of-scope test",
    summary: "The Judge gate rejected a PASS. A stale assertion owned by another objective failed.",
    root_cause: "stale cross-objective assertion",
  },
  mission_failure: {
    type: "mission_failure",
    mission_id: "msn_example",
    vessel: "ExampleVessel",
    failure_reason: "claude result error (1 turns). Provider returned HTTP 423.",
  },
  mail: {
    type: "mail",
    mission_id: "msn_example",
    vessel: "ExampleVessel",
    payload: "Preserve the byte-exact frames while fixing the response polarity.",
  },
  preflightFailed: {
    type: "preflight",
    objective_id: "obj_example",
    vessel: "ExampleVessel",
    title: "Port the Eaton calibration reorder",
    failed_questions: [4, 13],
    platform_said: "objective_preflight_incomplete: q4,q13",
    disposition: "held: owner question",
  },
  preflightPass: {
    type: "preflight",
    objective_id: "obj_example",
    vessel: "ExampleVessel",
    title: "Port a decoder family",
    failed_questions: [],
    platform_said: "ready",
    disposition: "dispatched",
  },
};

// 1. Every input shape yields a DRAFT line — the unconditional guarantee.
for (const [name, input] of Object.entries(inputs)) {
  const line = draftCorpusLine(input, null, fixedNow);
  check("draft flag is true for " + name, line[DRAFT_FLAG] === true);
  check("no answer is pre-decided for " + name, line.decided === "" && line.decided_by === null && line.basis === "");
}

// 2. Kind inference.
check("incident infers failure_class", draftCorpusLine(inputs.incident, null, fixedNow).kind === "failure_class");
check("mission_failure infers failure_class", draftCorpusLine(inputs.mission_failure, null, fixedNow).kind === "failure_class");
check("mail infers operator_mail", draftCorpusLine(inputs.mail, null, fixedNow).kind === "operator_mail");
check("preflight infers preflight", draftCorpusLine(inputs.preflightFailed, null, fixedNow).kind === "preflight");
check("an unknown/valid kind override is honoured", draftCorpusLine(inputs.mail, "refusal", fixedNow).kind === "refusal");
check("an invalid kind override falls back to inference", draftCorpusLine(inputs.mail, "not_a_kind", fixedNow).kind === "operator_mail");

// 3. The one deterministic label: preflight preventable_in_brief follows failed questions.
check("preflight with failed questions is preventable_in_brief", draftCorpusLine(inputs.preflightFailed, null, fixedNow).preventable_in_brief === true);
check("preflight pass is not preventable_in_brief", draftCorpusLine(inputs.preflightPass, null, fixedNow).preventable_in_brief === false);
check("preflight options carry the failed question numbers", JSON.stringify(draftCorpusLine(inputs.preflightFailed, null, fixedNow).options) === "[4,13]");
check("non-preflight leaves preventable_in_brief null for the operator", draftCorpusLine(inputs.incident, null, fixedNow).preventable_in_brief === null);

// 4. Ids are carried through; missing ids are null, never invented.
const inc = draftCorpusLine(inputs.incident, null, fixedNow);
check("incident id carried", inc.incident_id === "inc_example" && inc.mission_id === "msn_example");
const mail = draftCorpusLine(inputs.mail, null, fixedNow);
check("absent objective/incident ids are null", mail.objective_id === null && mail.incident_id === null);

// 5. A confirmed corpus line (no draft flag) is never produced by any shape; the only
//    difference between a draft and a confirmed line is the presence of the flag.
let anyConfirmed = false;
for (const input of Object.values(inputs)) {
  const line = draftCorpusLine(input, null, fixedNow);
  if (!(DRAFT_FLAG in line) || line[DRAFT_FLAG] !== true) anyConfirmed = true;
}
check("no shape ever yields a confirmed line", anyConfirmed === false);

// 6. Malformed input does not throw and still yields a draft.
check("empty object still drafts a draft line", draftCorpusLine({}, null, fixedNow)[DRAFT_FLAG] === true);
check("null input still drafts a draft line", draftCorpusLine(null, null, fixedNow)[DRAFT_FLAG] === true);

// 7. End-to-end: the CLI reads stdin and emits exactly one draft line on stdout.
try {
  const here = dirname(fileURLToPath(import.meta.url));
  const script = join(here, "draft-corpus-line.mjs");
  const out = execFileSync("node", [script], { input: JSON.stringify(inputs.mission_failure), encoding: "utf8" });
  const lines = out.trim().split("\n");
  const parsed = JSON.parse(lines[0]);
  check("CLI emits one line", lines.length === 1);
  check("CLI line is a draft", parsed[DRAFT_FLAG] === true);
  check("CLI line has no pre-decided answer", parsed.decided === "" && parsed.decided_by === null);
} catch (err) {
  check("CLI end-to-end run", false);
  process.stderr.write(String(err) + "\n");
}

process.stdout.write((failures === 0 ? "RESULT: PASS" : "RESULT: FAIL (" + failures + ")") + "\n");
process.exit(failures === 0 ? 0 : 1);
