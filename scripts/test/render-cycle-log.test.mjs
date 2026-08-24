import assert from "node:assert/strict";
import test from "node:test";

import { renderStream } from "../autonomy/render-cycle-log.mjs";

const lines = (...events) => events.map((e) => JSON.stringify(e));

test("renders the shape a real cycle produces", () => {
  const out = renderStream(lines(
    { type: "system", subtype: "init", session_id: "abc", cwd: "/srv/x", tools: ["Bash", "Read"] },
    { type: "assistant", message: { role: "assistant", content: [{ type: "text", text: "Reading the board." }] } },
    {
      type: "assistant",
      message: {
        role: "assistant",
        content: [{ type: "tool_use", id: "t1", name: "Bash", input: { command: "git status" } }],
      },
    },
    { type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: "t1", content: "clean" }] } },
    { type: "result", subtype: "success", num_turns: 4, duration_ms: 8400 },
  ));

  assert.match(out, /^\[init\] {3}session=abc cwd=\/srv\/x tools=2$/m);
  assert.match(out, /^\[say] {4}Reading the board\.$/m);
  assert.match(out, /^\[tool] {3}Bash: git status$/m);
  assert.match(out, /^\[ok] {5}Bash: clean$/m);
  assert.match(out, /^\[result] success turns=4 duration=8\.4s tool_calls=1 tool_errors=0$/m);
});

test("marks a failed tool result and counts it", () => {
  const out = renderStream(lines(
    {
      type: "assistant",
      message: { role: "assistant", content: [{ type: "tool_use", id: "t1", name: "Bash", input: { command: "false" } }] },
    },
    {
      type: "user",
      message: { role: "user", content: [{ type: "tool_result", tool_use_id: "t1", content: "boom", is_error: true }] },
    },
    { type: "result", subtype: "success", num_turns: 2, duration_ms: 1000 },
  ));

  assert.match(out, /^\[ERROR] {2}Bash: boom$/m);
  assert.match(out, /tool_errors=1/);
});

test("a stream with no result event is reported as incomplete", () => {
  // This is the timeout case. A log that simply stops looks the same as a log that
  // finished, so the difference has to be stated.
  const out = renderStream(lines(
    { type: "assistant", message: { role: "assistant", content: [{ type: "text", text: "working" }] } },
  ));

  assert.match(out, /\[result] INCOMPLETE: the stream ended with no result event/);
  assert.match(out, /killed, timed out, or the runtime died/);
});

test("keeps non-JSON lines instead of dropping them", () => {
  // A runtime error prints plain text to the same stream. Dropping it is how a failed
  // cycle produced an empty log in the first place.
  const out = renderStream(["Error: Invalid MCP configuration:", ""]);
  assert.match(out, /^\[raw] {4}Error: Invalid MCP configuration:$/m);
});

test("collapses multi-line text so one event stays one line", () => {
  const out = renderStream(lines(
    { type: "assistant", message: { role: "assistant", content: [{ type: "text", text: "line one\n\nline  two" }] } },
    { type: "result", subtype: "success", num_turns: 1, duration_ms: 10 },
  ));
  assert.match(out, /^\[say] {4}line one line two$/m);
});

test("truncates a large tool payload but keeps the useful field", () => {
  const long = "x".repeat(5000);
  const out = renderStream(lines(
    {
      type: "assistant",
      message: {
        role: "assistant",
        content: [{ type: "tool_use", id: "t1", name: "Write", input: { file_path: "/tmp/a", content: long } }],
      },
    },
    { type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: "t1", content: long }] } },
    { type: "result", subtype: "success", num_turns: 1, duration_ms: 10 },
  ));

  // file_path is chosen over the 5000-char body.
  assert.match(out, /^\[tool] {3}Write: \/tmp\/a$/m);
  assert.match(out, /\(\+\d+ chars\)/);
  for (const line of out.split("\n")) assert.ok(line.length < 600, `line too long: ${line.length}`);
});

test("reports a non-success result with its message", () => {
  const out = renderStream(lines(
    { type: "result", subtype: "error_max_turns", num_turns: 99, duration_ms: 500, result: "hit the turn limit" },
  ));
  assert.match(out, /\[result] error_max_turns turns=99/);
  assert.match(out, /hit the turn limit/);
});
