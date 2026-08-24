#!/usr/bin/env node
//
// Render a Claude stream-json or OpenCode JSON transcript into a readable cycle log.
//
// WHY: `claude --print` emits only the FINAL assistant message. One autonomous cycle
// ran for eight minutes, did real work, and left a 73-byte log saying it had nothing
// to report. The work was visible only on the coordination board, so a cycle that
// misbehaved would leave almost nothing to read afterwards.
//
// The raw .jsonl stays on disk as the source of truth. This produces the human-sized
// version beside it: what it said, which tools it called, whether each one worked, and
// how the run ended.
//
// Usage:
//   render-cycle-log.mjs < cycle.jsonl > cycle.log
//   render-cycle-log.mjs cycle.jsonl   > cycle.log

const MAX_TEXT = 2000;
const MAX_TOOL_INPUT = 200;
const MAX_TOOL_RESULT = 300;

function collapse(value, limit) {
  const text = String(value ?? "").replace(/\s+/g, " ").trim();
  if (text.length <= limit) return text;
  return `${text.slice(0, limit)}... (+${text.length - limit} chars)`;
}

function describeToolInput(name, input) {
  if (!input || typeof input !== "object") return "";
  // Show the field that says what the call actually does, rather than the whole
  // payload: a brief or a file body would bury the line it belongs to.
  for (const key of ["command", "pattern", "file_path", "path", "query", "content", "prompt"]) {
    if (typeof input[key] === "string" && input[key].length > 0) {
      return collapse(input[key], MAX_TOOL_INPUT);
    }
  }
  const keys = Object.keys(input);
  if (keys.length === 0) return "";
  return collapse(JSON.stringify(input), MAX_TOOL_INPUT);
}

export function renderStream(lines) {
  const out = [];
  const toolNamesById = new Map();
  let toolCalls = 0;
  let toolErrors = 0;
  let openCodeSeen = false;
  let openCodeSteps = 0;
  let openCodeCost = 0;
  let openCodeReason = "complete";
  let openCodeFinished = false;

  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed.length === 0) continue;

    let event;
    try {
      event = JSON.parse(trimmed);
    } catch {
      // A non-JSON line is usually a runtime error printed to the same stream.
      // Keep it: losing it is how a failed cycle becomes an empty log.
      out.push(`[raw]    ${collapse(trimmed, MAX_TEXT)}`);
      continue;
    }

    if (event.type === "system" && event.subtype === "init") {
      const toolCount = Array.isArray(event.tools) ? event.tools.length : 0;
      // The model is NOT pinned by the launcher: it follows the CLI's configured
      // default, so a change to that default would silently change which model
      // runs unattended. Recording it per cycle makes that visible in the log
      // rather than something to go and ask the CLI about afterwards.
      out.push(
        `[init]   session=${event.session_id ?? "?"} model=${event.model ?? "?"} ` +
        `cwd=${event.cwd ?? "?"} tools=${toolCount}`,
      );
      continue;
    }

    // OpenCode emits one JSON object for each part. A tool part can be emitted
    // more than once as its state changes, so count and print its invocation
    // only once. Keep its completed or failed state as a separate result line.
    if (event.type === "step_start") {
      openCodeSeen = true;
      openCodeFinished = false;
      openCodeSteps++;
      if (openCodeSteps === 1) {
        out.push(`[init]   session=${event.sessionID ?? event.session_id ?? "?"} runtime=opencode`);
      }
      continue;
    }

    if (event.type === "text") {
      openCodeSeen = true;
      const body = event.part?.text ?? event.text;
      if (body?.trim()) out.push(`[say]    ${collapse(body, MAX_TEXT)}`);
      continue;
    }

    if (event.type === "tool_use") {
      openCodeSeen = true;
      const part = event.part ?? event;
      const state = part.state ?? {};
      const id = part.callID ?? part.call_id ?? part.id ?? `${part.tool ?? part.name ?? "tool"}-${toolCalls}`;
      const name = part.tool ?? part.name ?? "tool";
      if (!toolNamesById.has(id)) {
        toolCalls++;
        toolNamesById.set(id, name);
        const detail = describeToolInput(name, state.input ?? part.input);
        out.push(`[tool]   ${name}${detail ? `: ${detail}` : ""}`);
      }
      if (state.status === "completed") {
        out.push(`[ok]     ${name}: ${collapse(state.output, MAX_TOOL_RESULT)}`);
      } else if (state.status === "error" || state.status === "failed") {
        toolErrors++;
        out.push(`[ERROR]  ${name}: ${collapse(state.error ?? state.output, MAX_TOOL_RESULT)}`);
      }
      continue;
    }

    if (event.type === "step_finish") {
      openCodeSeen = true;
      openCodeFinished = true;
      const part = event.part ?? event;
      openCodeReason = part.reason ?? event.reason ?? openCodeReason;
      if (typeof part.cost === "number") openCodeCost += part.cost;
      continue;
    }

    if (event.type === "error") {
      openCodeSeen = true;
      openCodeFinished = true;
      openCodeReason = "error";
      out.push(`[ERROR]  opencode: ${collapse(event.error?.message ?? event.message ?? event.error, MAX_TEXT)}`);
      continue;
    }

    if (event.type === "assistant") {
      for (const block of event.message?.content ?? []) {
        if (block.type === "text" && block.text?.trim()) {
          out.push(`[say]    ${collapse(block.text, MAX_TEXT)}`);
        } else if (block.type === "tool_use") {
          toolCalls++;
          toolNamesById.set(block.id, block.name);
          const detail = describeToolInput(block.name, block.input);
          out.push(`[tool]   ${block.name}${detail ? `: ${detail}` : ""}`);
        }
      }
      continue;
    }

    if (event.type === "user") {
      for (const block of event.message?.content ?? []) {
        if (block.type !== "tool_result") continue;
        const name = toolNamesById.get(block.tool_use_id) ?? "tool";
        const body = Array.isArray(block.content)
          ? block.content.map((part) => (typeof part === "string" ? part : part?.text ?? "")).join(" ")
          : block.content;
        if (block.is_error) {
          toolErrors++;
          out.push(`[ERROR]  ${name}: ${collapse(body, MAX_TOOL_RESULT)}`);
        } else {
          out.push(`[ok]     ${name}: ${collapse(body, MAX_TOOL_RESULT)}`);
        }
      }
      continue;
    }

    if (event.type === "result") {
      const seconds = typeof event.duration_ms === "number"
        ? (event.duration_ms / 1000).toFixed(1)
        : "?";
      out.push(
        `[result] ${event.subtype ?? "?"} turns=${event.num_turns ?? "?"} duration=${seconds}s ` +
        `tool_calls=${toolCalls} tool_errors=${toolErrors}`,
      );
      if (event.subtype !== "success" && event.result) {
        out.push(`[result] ${collapse(event.result, MAX_TEXT)}`);
      }
    }
  }

  // A stream that ends without a result event means the run was killed -- a timeout
  // cap, or the host going away. Say so, rather than letting the log just stop.
  if (openCodeSeen && openCodeFinished && !out.some((entry) => entry.startsWith("[result]"))) {
    out.push(
      `[result] ${openCodeReason} steps=${openCodeSteps} cost=$${openCodeCost.toFixed(6)} ` +
      `tool_calls=${toolCalls} tool_errors=${toolErrors}`,
    );
  } else if (!out.some((entry) => entry.startsWith("[result]"))) {
    out.push(`[result] INCOMPLETE: the stream ended with no result event ` +
      `(killed, timed out, or the runtime died). tool_calls=${toolCalls} tool_errors=${toolErrors}`);
  }

  return out.join("\n");
}

const isMainModule = process.argv[1] && import.meta.url === `file://${process.argv[1]}`;
if (isMainModule) {
  const { readFileSync } = await import("node:fs");
  const source = process.argv[2] ? readFileSync(process.argv[2], "utf8") : readFileSync(0, "utf8");
  process.stdout.write(`${renderStream(source.split("\n"))}\n`);
}
