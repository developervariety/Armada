#!/usr/bin/env node
// Self-check for draft-corpus-line.mjs (D14 corpus_prelabel).
//
// Run:  node scripts/autonomy/test-draft-corpus-line.mjs
// Exit: 0 when every check passes, 1 otherwise. No arguments, no outbound network, no
// files written. Every model call is injected, except one transport check that runs
// the whole script against a loopback stub of the admiral, so the suite never reaches
// a provider or a real admiral.
//
// The load-bearing guarantees it proves:
//   1. Every emitted line carries "draft": true -- on the model path, on the
//      fail-closed path, and when the model answers with high confidence.
//   2. No failure path ever proposes a kind; each states its reason instead.
//   3. State is redacted before egress, and only its hash and byte count are recorded.
//   4. The corpus kind -> scored decision map does not drift from the eval-store
//      ingester that maps the same kinds.
//
// Set ARMADA_CORPUS_INGESTER to the eval-store ingester file to run the cross-repo
// map comparison; without it that check reports SKIP with its reason, never PASS.

import {
  draftCorpusLine,
  resolveProvisionalKind,
  redactForEgress,
  parseIngesterKindMap,
  DRAFT_FLAG,
  DECISION_KEY,
  CORPUS_KINDS,
  KIND_TO_DECISION,
  KIND_SOURCE,
} from "./draft-corpus-line.mjs";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

let failures = 0;
let skips = 0;
function check(name, condition) {
  if (condition) {
    process.stdout.write("  PASS  " + name + "\n");
  } else {
    failures++;
    process.stdout.write("  FAIL  " + name + "\n");
  }
}

// A skipped check is never a pass. It names why it did not run, so an empty run cannot
// read as a green one.
function skip(name, reason) {
  skips++;
  process.stdout.write("  SKIP  " + name + " -- " + reason + "\n");
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
    failure_reason: "agent result error (1 turns). Provider returned HTTP 423.",
  },
  mail: {
    type: "mail",
    mission_id: "msn_example",
    vessel: "ExampleVessel",
    payload: "Keep the existing output format while fixing the ordering.",
  },
  preflightFailed: {
    type: "preflight",
    objective_id: "obj_example",
    vessel: "ExampleVessel",
    title: "Extend the example adapter",
    failed_questions: [4, 13],
    platform_said: "objective_preflight_incomplete: q4,q13",
    disposition: "held: owner question",
  },
  preflightPass: {
    type: "preflight",
    objective_id: "obj_example",
    vessel: "ExampleVessel",
    title: "Extend a second example adapter",
    failed_questions: [],
    platform_said: "ready",
    disposition: "dispatched",
  },
};

// Outcome shapes the drafter receives from resolveProvisionalKind.
const modelKind = { kind: "failure_class", source: KIND_SOURCE.model, confidence: 0.97, reason: null, stateSha256: "a".repeat(64), stateBytes: 120 };
const unavailableKind = { kind: null, source: KIND_SOURCE.unavailable, confidence: null, reason: "model_unavailable: timeout", stateSha256: null, stateBytes: 0 };

// ---------------------------------------------------------------- shape guarantees

// 1. Every input shape yields a DRAFT line, whatever the kind outcome.
for (const [name, input] of Object.entries(inputs)) {
  for (const [outcomeName, outcome] of [["model", modelKind], ["unavailable", unavailableKind]]) {
    const line = draftCorpusLine(input, outcome, fixedNow);
    check("draft flag is true for " + name + " on the " + outcomeName + " path", line[DRAFT_FLAG] === true);
    check("no answer is pre-decided for " + name + " on the " + outcomeName + " path",
      line.decided === "" && line.decided_by === null && line.basis === "");
  }
}

// 2. A high-confidence model answer is still a draft; the script never confirms one.
const confident = draftCorpusLine(inputs.incident, { ...modelKind, confidence: 1 }, fixedNow);
check("a model answer at full confidence is still a draft", confident[DRAFT_FLAG] === true);
check("a model answer at full confidence fills only the kind", confident.kind === "failure_class" && confident.decided === "");
check("the draft names the decision that produced it", confident.draft_meta.decision === DECISION_KEY);
check("the draft carries the model confidence", confident.draft_meta.kind_confidence === 1);

// 3. A fail-closed outcome proposes NO kind and states its reason.
const failed = draftCorpusLine(inputs.mission_failure, unavailableKind, fixedNow);
check("a fail-closed draft proposes no kind", failed.kind === null);
check("a fail-closed draft states its reason", failed.draft_meta.kind_reason === "model_unavailable: timeout");
check("a fail-closed draft is still a draft", failed[DRAFT_FLAG] === true);
check("a fail-closed draft names no scored decision, with a reason",
  failed.draft_meta.scored_decision === null && typeof failed.draft_meta.scored_decision_reason === "string" && failed.draft_meta.scored_decision_reason.length > 0);

// 4. The egress record carries the hash and the byte count, never the state.
const recorded = draftCorpusLine(inputs.incident, modelKind, fixedNow);
const recordedText = JSON.stringify(recorded);
check("the draft records the state hash", recorded.draft_meta.state_sha256 === "a".repeat(64));
check("the draft records the state byte count", recorded.draft_meta.state_bytes === 120);
check("draft_meta carries the state's hash and size and nothing else of it",
  JSON.stringify(Object.keys(recorded.draft_meta).sort()) === JSON.stringify([
    "decision", "kind_confidence", "kind_reason", "kind_source", "scored_decision", "scored_decision_reason", "state_bytes", "state_sha256",
  ]));
check("draft_meta never carries the state text", !JSON.stringify(recorded.draft_meta).includes(inputs.incident.summary.slice(0, 30)));
check("the drafted line is one JSON line", !recordedText.includes("\n"));

// 5. The one deterministic label: preflight preventable_in_brief follows failed questions.
const preflightDraft = draftCorpusLine(inputs.preflightFailed, { ...modelKind, kind: "preflight" }, fixedNow);
check("preflight with failed questions is preventable_in_brief", preflightDraft.preventable_in_brief === true);
check("preflight pass is not preventable_in_brief",
  draftCorpusLine(inputs.preflightPass, { ...modelKind, kind: "preflight" }, fixedNow).preventable_in_brief === false);
check("preflight options carry the failed question numbers", JSON.stringify(preflightDraft.options) === "[4,13]");
check("a non-preflight kind leaves preventable_in_brief null for the operator",
  draftCorpusLine(inputs.incident, modelKind, fixedNow).preventable_in_brief === null);
check("an unresolved kind leaves preventable_in_brief null",
  draftCorpusLine(inputs.preflightFailed, unavailableKind, fixedNow).preventable_in_brief === null);

// 6. Ids are carried through; missing ids are null, never invented.
check("incident id carried", recorded.incident_id === "inc_example" && recorded.mission_id === "msn_example");
const mail = draftCorpusLine(inputs.mail, unavailableKind, fixedNow);
check("absent objective/incident ids are null", mail.objective_id === null && mail.incident_id === null);

// 7. Malformed input does not throw and still yields a draft.
check("empty object still drafts a draft line", draftCorpusLine({}, unavailableKind, fixedNow)[DRAFT_FLAG] === true);
check("null input still drafts a draft line", draftCorpusLine(null, unavailableKind, fixedNow)[DRAFT_FLAG] === true);
check("a missing kind outcome still drafts a draft line with a reason", (() => {
  const line = draftCorpusLine(inputs.mail, null, fixedNow);
  return line[DRAFT_FLAG] === true && line.kind === null && typeof line.draft_meta.kind_reason === "string" && line.draft_meta.kind_reason.length > 0;
})());

// ------------------------------------------------------------------- redaction

const dirty = {
  failure_reason: "mission msn_abc123def failed at /srv/example/work/file.cs against https://example.invalid/repo with key sk-exampleonly and commit 1a2b3c4d5e6f7a8",
};
const redacted = redactForEgress(dirty, 4000);
const redactedText = redacted.text;
check("redaction removes Armada ids", !redactedText.includes("msn_abc123def"));
check("redaction removes absolute paths", !redactedText.includes("/srv/example"));
check("redaction removes hosts and URLs", !redactedText.includes("example.invalid"));
check("redaction removes key-shaped tokens", !redactedText.includes("sk-exampleonly"));
check("redaction removes commit-shaped hex", !redactedText.includes("1a2b3c4d5e6f7a8"));
check("redaction reports a sha256 and a byte count", /^[0-9a-f]{64}$/.test(redacted.sha256) && redacted.bytes === Buffer.byteLength(redactedText, "utf8"));
check("redaction truncates to the byte budget", redactForEgress({ text: "x".repeat(10000) }, 500).text.length <= 500);

// --------------------------------------------------- resolveProvisionalKind paths

const gateStatus = {
  effectiveMode: "Gate",
  effectiveReason: null,
  keyPresent: true,
  decisions: [{ key: DECISION_KEY, mode: "Gate", threshold: 0.9, description: "" }],
};

function answering(kind, confidence) {
  return async () => ({ available: true, answers: { provisional_kind: { type: "choice", choice: kind, confidence } } });
}

async function resolved(options) {
  return await resolveProvisionalKind(inputs.mission_failure, {
    fetchStatus: async () => gateStatus,
    askModel: answering("failure_class", 0.95),
    ...options,
  });
}

const modelPath = await resolved({});
check("a gated, above-threshold answer fills the kind", modelPath.kind === "failure_class" && modelPath.source === KIND_SOURCE.model);
check("the model path reports the state hash and byte count",
  /^[0-9a-f]{64}$/.test(String(modelPath.stateSha256)) && modelPath.stateBytes > 0);

const belowThreshold = await resolved({ askModel: answering("failure_class", 0.42) });
check("an answer below the gate threshold proposes no kind", belowThreshold.kind === null);
check("an answer below the gate threshold states its reason", /below_gate_threshold/.test(String(belowThreshold.reason)));

const unknownChoice = await resolved({ askModel: answering("not_a_corpus_kind", 0.99) });
check("a choice outside the corpus kinds proposes no kind", unknownChoice.kind === null);
check("a choice outside the corpus kinds states its reason", /unknown_kind/.test(String(unknownChoice.reason)));

const noConfidence = await resolved({ askModel: async () => ({ available: true, answers: { provisional_kind: { choice: "refusal" } } }) });
check("an answer with no confidence proposes no kind", noConfidence.kind === null && /confidence/.test(String(noConfidence.reason)));

const parseError = await resolved({ askModel: async () => ({ available: true, answers: null }) });
check("an unparsable answer proposes no kind with a reason", parseError.kind === null && /parse|answer/.test(String(parseError.reason)));

const transportFailures = [
  ["timeout", new Error("request timed out after 20000 ms")],
  ["non-2xx", new Error("armada MCP returned HTTP 500")],
  ["429", new Error("armada MCP returned HTTP 429")],
  ["529", new Error("armada MCP returned HTTP 529")],
];
for (const [name, error] of transportFailures) {
  const outcome = await resolved({ askModel: async () => { throw error; } });
  check("a " + name + " failure proposes no kind", outcome.kind === null);
  check("a " + name + " failure states its reason", typeof outcome.reason === "string" && outcome.reason.length > 0);
  check("a " + name + " failure still drafts a draft line", draftCorpusLine(inputs.mission_failure, outcome, fixedNow)[DRAFT_FLAG] === true);
}

const unavailableTool = await resolved({ askModel: async () => ({ available: false, reason: "disabled", message: "The typed-decision tool is not enabled." }) });
check("an unavailable tool proposes no kind", unavailableTool.kind === null);
check("an unavailable tool carries the tool's own reason", /disabled/.test(String(unavailableTool.reason)));

const noKey = await resolved({
  fetchStatus: async () => ({ ...gateStatus, effectiveMode: "Off", effectiveReason: "typed_decisions_no_key", keyPresent: false }),
});
check("no key proposes no kind", noKey.kind === null);
check("no key states typed_decisions_no_key", /typed_decisions_no_key/.test(String(noKey.reason)));

const decisionOff = await resolved({
  fetchStatus: async () => ({ ...gateStatus, decisions: [{ key: DECISION_KEY, mode: "Off", threshold: 0.9 }] }),
});
check("the decision set Off proposes no kind", decisionOff.kind === null && /off/i.test(String(decisionOff.reason)));

const shadow = await resolved({
  fetchStatus: async () => ({ ...gateStatus, decisions: [{ key: DECISION_KEY, mode: "Shadow", threshold: 0.9 }] }),
});
check("a Shadow decision records but proposes no kind", shadow.kind === null && /shadow/i.test(String(shadow.reason)));

const statusDown = await resolved({ fetchStatus: async () => { throw new Error("connect refused"); } });
check("an unreachable status endpoint proposes no kind", statusDown.kind === null && /status/i.test(String(statusDown.reason)));

const missingEntry = await resolved({ fetchStatus: async () => ({ ...gateStatus, decisions: [] }) });
check("a decision absent from settings proposes no kind", missingEntry.kind === null && /settings|unknown/i.test(String(missingEntry.reason)));

const offline = await resolveProvisionalKind(inputs.mail, { noModel: true });
check("--no-model proposes no kind and says so", offline.kind === null && /not_requested/.test(String(offline.reason)));

const override = await resolveProvisionalKind(inputs.mail, { kindOverride: "refusal", noModel: true });
check("an operator kind override is honoured", override.kind === "refusal" && override.source === KIND_SOURCE.operator);
const badOverride = await resolveProvisionalKind(inputs.mail, { kindOverride: "not_a_kind", noModel: true });
check("an invalid kind override proposes no kind and says why", badOverride.kind === null && /override/.test(String(badOverride.reason)));

// A model call must never see an unredacted state.
let seenState = null;
await resolveProvisionalKind({ type: "mission_failure", mission_id: "msn_secretid", failure_reason: "failed in /srv/example/work" }, {
  fetchStatus: async () => gateStatus,
  askModel: async (state) => { seenState = JSON.stringify(state); return { available: true, answers: { provisional_kind: { choice: "failure_class", confidence: 0.99 } } }; },
});
check("the model never sees an Armada id", seenState !== null && !seenState.includes("msn_secretid"));
check("the model never sees a host path", seenState !== null && !seenState.includes("/srv/example"));

// ------------------------------------------------- kind map: one definition, no drift

check("every corpus kind has a mapping entry", CORPUS_KINDS.every((kind) => Object.prototype.hasOwnProperty.call(KIND_TO_DECISION, kind)));
check("an unscored kind maps to null, never to a guess",
  Object.values(KIND_TO_DECISION).every((value) => value === null || (typeof value === "string" && value.length > 0)));

const ingesterPath = process.env.ARMADA_CORPUS_INGESTER;
if (!ingesterPath) {
  skip("kind map matches the eval-store ingester",
    "ARMADA_CORPUS_INGESTER is not set; the ingester lives outside this repository and is not compared in this run");
} else {
  try {
    const theirs = parseIngesterKindMap(readFileSync(ingesterPath, "utf8"));
    const mine = Object.fromEntries(Object.entries(KIND_TO_DECISION).filter(([, value]) => value !== null));
    check("kind map matches the eval-store ingester", JSON.stringify(theirs) === JSON.stringify(mine));
    if (JSON.stringify(theirs) !== JSON.stringify(mine)) {
      process.stdout.write("        ingester: " + JSON.stringify(theirs) + "\n        drafter:  " + JSON.stringify(mine) + "\n");
    }
  } catch (err) {
    check("kind map matches the eval-store ingester", false);
    process.stderr.write(String(err) + "\n");
  }
}

// A parser check that runs everywhere, so the comparison itself is proven even when the
// ingester file is absent.
check("the ingester map parser reads a KIND_MAP literal", (() => {
  const parsed = parseIngesterKindMap('KIND_MAP = {\n "failure_class": "failure_cause",\n # a comment\n "refusal": "refusal",\n}\n');
  return JSON.stringify(parsed) === JSON.stringify({ failure_class: "failure_cause", refusal: "refusal" });
})());

// ------------------------------------------------------------------ end to end

try {
  const here = dirname(fileURLToPath(import.meta.url));
  const script = join(here, "draft-corpus-line.mjs");
  const out = execFileSync("node", [script, "--no-model"], { input: JSON.stringify(inputs.mission_failure), encoding: "utf8" });
  const lines = out.trim().split("\n");
  const parsed = JSON.parse(lines[0]);
  check("CLI emits one line", lines.length === 1);
  check("CLI line is a draft", parsed[DRAFT_FLAG] === true);
  check("CLI line has no pre-decided answer", parsed.decided === "" && parsed.decided_by === null);
  check("CLI offline line proposes no kind and states why", parsed.kind === null && typeof parsed.draft_meta.kind_reason === "string");
} catch (err) {
  check("CLI end-to-end run", false);
  process.stderr.write(String(err) + "\n");
}

// ------------------------------------------------- transport, against a stub admiral
//
// Proves the shipped request path, not a mock of it: the status read, the MCP
// tools/call, an event-stream reply, and that the state reaching the wire is redacted.

async function runAgainstStubAdmiral() {
  const { createServer } = await import("node:http");
  const { execFile } = await import("node:child_process");

  const status = {
    effectiveMode: "Gate",
    effectiveReason: null,
    keyPresent: true,
    decisions: [{ key: DECISION_KEY, mode: "Gate", threshold: 0.9, description: "" }],
  };

  let toolName = null;
  let stateOnTheWire = null;
  const server = createServer((req, res) => {
    let body = "";
    req.on("data", (chunk) => { body += chunk; });
    req.on("end", () => {
      if (req.headers["x-api-key"] !== "stub-key") { res.writeHead(401); res.end("unauthorized"); return; }
      if (req.url.startsWith("/api/v1/typed-decisions")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify(status));
        return;
      }
      const call = JSON.parse(body);
      toolName = call.params.name;
      stateOnTheWire = JSON.stringify(call.params.arguments.state);
      const answer = { available: true, answers: { provisional_kind: { type: "choice", choice: "failure_class", confidence: 0.96 } } };
      const result = { content: [{ type: "text", text: JSON.stringify(answer) }] };
      // The event-stream shape, which is the harder of the two replies to read.
      res.writeHead(200, { "Content-Type": "text/event-stream" });
      res.end("event: message\ndata: " + JSON.stringify({ jsonrpc: "2.0", id: call.id, result }) + "\n\n");
    });
  });

  const listening = await new Promise((resolve) => {
    server.once("error", (err) => resolve({ error: err }));
    server.listen(0, "127.0.0.1", () => resolve({ port: server.address().port }));
  });
  if (listening.error) {
    server.close();
    return { skipped: "a loopback port could not be opened: " + listening.error.message };
  }

  const here = dirname(fileURLToPath(import.meta.url));
  const script = join(here, "draft-corpus-line.mjs");
  const base = "http://127.0.0.1:" + listening.port;
  const run = await new Promise((resolve) => {
    const child = execFile("node", [script], {
      env: { ...process.env, ARMADA_API_KEY: "stub-key", ARMADA_API_URL: base, ARMADA_MCP_URL: base + "/mcp" },
    }, (err, stdout, stderr) => resolve({ err, stdout: String(stdout), stderr: String(stderr) }));
    child.stdin.end(JSON.stringify({ type: "mission_failure", mission_id: "msn_example", failure_reason: "the run failed at /srv/example/work/file.cs" }));
  });
  server.close();
  return { ...run, toolName, stateOnTheWire };
}

const stub = await runAgainstStubAdmiral();
if (stub.skipped) {
  skip("the script answers over the admiral's typed-decision tool", stub.skipped);
} else {
  const line = stub.err ? null : JSON.parse(stub.stdout.trim());
  check("the script calls the admiral's typed-decision tool", stub.toolName === "armada_typed_decision");
  check("the state on the wire is redacted", stub.stateOnTheWire !== null && !stub.stateOnTheWire.includes("/srv/example"));
  check("a gated answer over the real transport fills the kind", line !== null && line.kind === "failure_class");
  check("a gated answer over the real transport is still a draft", line !== null && line[DRAFT_FLAG] === true);
  check("a gated answer over the real transport records hash and bytes",
    line !== null && /^[0-9a-f]{64}$/.test(String(line.draft_meta.state_sha256)) && line.draft_meta.state_bytes > 0);
  check("a gated answer over the real transport still decides nothing", line !== null && line.decided === "" && line.decided_by === null);
}

process.stdout.write((failures === 0 ? "RESULT: PASS" : "RESULT: FAIL (" + failures + ")")
  + (skips > 0 ? " with " + skips + " skipped" : "") + "\n");
process.exit(failures === 0 ? 0 : 1);
