// Armada context compaction for OpenCode captains.
//
// OpenCode calls `experimental.chat.messages.transform` before EVERY provider request with the whole
// message array, and edits made here reach the provider. Once the history passes a size where it is
// worth shedding, this asks the admiral which earlier tool results the captain still depends on. Those
// stay verbatim. Every other eligible tool output is replaced by a one-line note saying the tool can be
// re-run. No message and no part is removed, and every word the captain and the user wrote survives.
//
// Decisions are remembered by call id, so a result is asked about once, and a compacted output is
// re-applied on every later request without another call. That holds whether OpenCode persists the
// edit or rebuilds the array from its store each time.
//
// Every failure leaves the history exactly as OpenCode built it, so OpenCode's own compaction still
// runs as it always has. The plugin holds no provider key: it calls the admiral as this captain, with
// the MCP credential the launch already carries.

// Recency is counted in TOOL PARTS, not messages: OpenCode keeps many tool calls inside one assistant
// message, so a run can hold a whole mission's work in two or three messages, and protecting "the last
// N messages" would protect everything.
const RECENT_TOOL_PARTS_KEPT = 8;
const MIN_OUTPUT_CHARS = 300;
const HEAD_CHARS = 400;
const GOAL_CHARS = 2000;
const MAX_ASKED = 16;
// Below this the history is left alone. An operator can tune it per launch with
// ARMADA_COMPACTION_SHED_CHARS; anything that is not a positive whole number keeps the default.
const DEFAULT_SHED_CHARS = 400000;
const MARKER = "[compacted]";
const TOOL_NAME = "armada_context_compaction";

// The note names WHAT ran and says its output was already used. A bare "re-run the tool if you still need
// its output" was measured to make a model redo its whole task after compaction - every call twice - which
// for a captain means repeating commands, not only reads.
const NOTE_CALL_CHARS = 160;

function noteFor(askedFor, bytes) {
  return MARKER + " " + askedFor.slice(0, NOTE_CALL_CHARS) + " already ran earlier in this run and its " + bytes +
    "-byte output was already used; it was removed to keep the context small. Do not run it again unless the remaining work needs that exact output.";
}

function sizeOf(messages) {
  let total = 0;
  for (const message of messages) {
    for (const part of message.parts || []) {
      if (part.type === "text" || part.type === "reasoning") total += (part.text || "").length;
      else if (part.type === "tool" && part.state && typeof part.state.output === "string") total += part.state.output.length;
    }
  }
  return total;
}

function goalOf(messages) {
  for (const message of messages) {
    if (message.info && message.info.role !== "user") continue;
    for (const part of message.parts || []) {
      if (part.type === "text" && (part.text || "").trim()) return part.text.slice(0, GOAL_CHARS);
    }
  }
  return "";
}

function shedChars() {
  const configured = Number.parseInt(process.env.ARMADA_COMPACTION_SHED_CHARS || "", 10);
  return Number.isInteger(configured) && configured > 0 ? configured : DEFAULT_SHED_CHARS;
}

function eligibleParts(messages) {
  const completed = [];
  // The first message is the mission brief: never a candidate, and never touched.
  for (let index = 1; index < messages.length; index++) {
    for (const part of messages[index].parts || []) {
      if (part.type !== "tool" || !part.state || part.state.status !== "completed") continue;
      if (typeof part.state.output !== "string") continue;
      completed.push({ part });
    }
  }
  const eligible = completed.slice(0, Math.max(0, completed.length - RECENT_TOOL_PARTS_KEPT));
  eligible.forEach((entry, position) => { entry.turnsAgo = completed.length - position; });
  return eligible;
}

function readToolAnswer(text) {
  let body = (text || "").trim();
  if (!body.startsWith("{")) {
    const data = body.split("\n").filter((line) => line.startsWith("data:")).map((line) => line.slice(5).trim());
    body = data.length > 0 ? data[data.length - 1] : "";
  }
  if (!body) return null;
  const rpc = JSON.parse(body);
  const blocks = rpc && rpc.result && rpc.result.content;
  if (!Array.isArray(blocks) || blocks.length === 0 || typeof blocks[0].text !== "string") return null;
  return JSON.parse(blocks[0].text);
}

async function askAdmiral(url, token, missionId, goal, asked) {
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: "application/json, text/event-stream",
      authorization: "Bearer " + token,
    },
    body: JSON.stringify({
      jsonrpc: "2.0",
      id: 1,
      method: "tools/call",
      params: { name: TOOL_NAME, arguments: { missionId, goal, candidates: asked } },
    }),
  });
  if (!response.ok) return null;
  const answer = readToolAnswer(await response.text());
  if (!answer) return null;
  const available = answer.Available !== undefined ? answer.Available : answer.available;
  if (available !== true) return null;
  const spared = answer.SparedPositions || answer.sparedPositions || [];
  return Array.isArray(spared) ? spared.filter((n) => Number.isInteger(n)) : [];
}

export const ArmadaContextCompaction = async () => {
  const url = process.env.ARMADA_MCP_URL;
  const token = process.env.ARMADA_MCP_TOKEN;
  const missionId = process.env.ARMADA_MISSION_ID;
  // Without these the admiral cannot be asked, so the plugin registers nothing and OpenCode is unchanged.
  if (!url || !token || !missionId) return {};

  const spared = new Set();
  const compacted = new Map();

  return {
    "experimental.chat.messages.transform": async (_input, output) => {
      try {
        const messages = output.messages || [];
        const eligible = eligibleParts(messages);

        // Re-apply every earlier decision first: cheap, and needs no call.
        for (const { part } of eligible) {
          const note = compacted.get(part.callID);
          if (note && part.state.output !== note) part.state.output = note;
        }

        if (sizeOf(messages) < shedChars()) return;

        const fresh = eligible.filter(({ part }) =>
          !spared.has(part.callID) &&
          !compacted.has(part.callID) &&
          part.state.output.length >= MIN_OUTPUT_CHARS &&
          !part.state.output.startsWith(MARKER));
        if (fresh.length === 0) return;

        const asked = fresh.slice(0, MAX_ASKED);
        let input = "";
        const payload = asked.map(({ part, turnsAgo }) => {
          try { input = JSON.stringify(part.state.input || {}); } catch { input = ""; }
          return {
            tool: part.tool || "tool",
            askedFor: ((part.tool || "tool") + " " + input).slice(0, HEAD_CHARS),
            outputHead: part.state.output.slice(0, HEAD_CHARS),
            outputBytes: part.state.output.length,
            turnsAgo,
          };
        });

        const keep = await askAdmiral(url, token, missionId, goalOf(messages), payload);
        if (keep === null) return;

        const kept = new Set(keep.filter((position) => position >= 0 && position < asked.length));
        asked.forEach(({ part }, position) => {
          if (kept.has(position)) {
            spared.add(part.callID);
            return;
          }
          const note = noteFor(payload[position].askedFor, part.state.output.length);
          compacted.set(part.callID, note);
          part.state.output = note;
        });
      } catch {
        // Never break the harness: the history stays exactly as OpenCode built it.
      }
    },
  };
};
