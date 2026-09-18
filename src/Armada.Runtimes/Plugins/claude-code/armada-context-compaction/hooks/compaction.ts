// Armada context compaction for Claude Code captains.
//
// Claude Code's own compaction already summarises the older part of a session and keeps the recent part
// verbatim, anchored by message id, and it resumes correctly from that. Replacing it with a hand-built
// message list was measured to lose that anchoring: the model restarted its task after compaction, and
// on a bad run looped until the harness aborted it as thrashing. So this plugin does not replace the
// harness's compaction. It STEERS it, with one call to `next`:
//
//   1. It asks the admiral which earlier tool results the captain still depends on (the context_compaction
//      typed decision), and tells the summariser to quote those verbatim.
//   2. It saves every earlier tool output to a file in the dock, in a directory whose own .gitignore ignores
//      everything, and tells the summariser to cite each file. Recovering a summarised output is then a read
//      of a named file, never a second run of a command that may have side effects.
//
// Every failure degrades to the harness's own compaction, unchanged. The plugin holds no provider key: it
// calls the admiral as this captain with the MCP credential the launch already carries.

const MIN_OUTPUT_CHARS = 300;
const HEAD_CHARS = 400;
const GOAL_CHARS = 2000;
const MAX_ASKED = 16;
const RECENT_EXCHANGES_SKIPPED = 3;
const VERBATIM_BUDGET_CHARS = 12000;
const INDEX_ENTRIES = 60;
const CALL_CHARS = 160;
const ARCHIVE_DIR = ".armada-compaction";
const TOOL_NAME = "armada_context_compaction";

function outputOf(messages: any[], toolUseId: string): string {
  let longest = "";
  for (const message of messages) {
    for (const use of message.toolUses ?? []) {
      if (use.tool_use_id === toolUseId && typeof use.text === "string" && use.text.length > longest.length) longest = use.text;
    }
    for (const result of message.toolResults ?? []) {
      if (result.tool_use_id === toolUseId && typeof result.text === "string" && result.text.length > longest.length) longest = result.text;
    }
  }
  return longest;
}

// The last few exchanges are what the harness keeps verbatim anyway, so the question is about the ones
// before them: those are the outputs a summary would otherwise lose.
function olderEnd(messages: any[]): number {
  let seen = 0;
  for (let index = messages.length - 1; index >= 1; index--) {
    if (messages[index].role === "assistant" && ++seen === RECENT_EXCHANGES_SKIPPED) return index;
  }
  return 1;
}

function collectCandidates(messages: any[], end: number): any[] {
  const candidates: any[] = [];
  const seen = new Set<string>();
  // The first message is the mission brief: never a candidate.
  for (let index = 1; index < end; index++) {
    for (const use of messages[index].toolUses ?? []) {
      const id = use.tool_use_id;
      if (!id || seen.has(id)) continue;
      seen.add(id);
      const output = outputOf(messages, id);
      if (output.length < MIN_OUTPUT_CHARS) continue;
      let asked = "";
      try { asked = (use.tool ?? "tool") + " " + JSON.stringify(use.input ?? {}); } catch { asked = use.tool ?? "tool"; }
      candidates.push({
        id,
        output,
        tool: use.tool ?? "tool",
        askedFor: asked.slice(0, HEAD_CHARS),
        outputHead: output.slice(0, HEAD_CHARS),
        outputBytes: output.length,
        turnsAgo: messages.length - index,
      });
    }
  }
  return candidates;
}

function goalOf(messages: any[]): string {
  for (const message of messages) {
    if (message.role === "user" && typeof message.text === "string" && message.text.trim().length > 0) {
      return message.text.slice(0, GOAL_CHARS);
    }
  }
  return "";
}

// A stateless MCP reply is JSON or one SSE frame; the tool's answer is the JSON text of the first content
// block (a wake banner may follow it as a second block and is ignored here).
function readToolAnswer(text: string): any {
  let body = (text ?? "").trim();
  if (!body.startsWith("{")) {
    const data = body.split("\n").filter((line) => line.startsWith("data:")).map((line) => line.slice(5).trim());
    body = data.length > 0 ? data[data.length - 1] : "";
  }
  if (!body) return null;
  const rpc = JSON.parse(body);
  const blocks = rpc?.result?.content;
  if (!Array.isArray(blocks) || blocks.length === 0 || typeof blocks[0]?.text !== "string") return null;
  return JSON.parse(blocks[0].text);
}

async function askAdmiral($: any, url: string, token: string, missionId: string, goal: string, asked: any[]): Promise<number[] | null> {
  const body = JSON.stringify({
    jsonrpc: "2.0",
    id: 1,
    method: "tools/call",
    params: {
      name: TOOL_NAME,
      arguments: {
        missionId,
        goal,
        candidates: asked.map((c) => ({ tool: c.tool, askedFor: c.askedFor, outputHead: c.outputHead, outputBytes: c.outputBytes, turnsAgo: c.turnsAgo })),
      },
    },
  });
  const response = await $.http.fetch(url, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json, text/event-stream", authorization: "Bearer " + token },
    body,
  });
  if (!response.ok) return null;
  const answer = readToolAnswer(response.text);
  if (!answer) return null;
  const available = answer.Available ?? answer.available;
  if (available !== true) return null;
  const spared = answer.SparedPositions ?? answer.sparedPositions ?? [];
  return Array.isArray(spared) ? spared.filter((n: any) => Number.isInteger(n)) : [];
}

function archivePathFor(id: string): string {
  return ARCHIVE_DIR + "/" + id.replace(/[^A-Za-z0-9_-]/g, "_") + ".txt";
}

async function archiveOutputs($: any, candidates: any[]): Promise<Map<string, string>> {
  const saved = new Map<string, string>();
  try {
    const ignore = ARCHIVE_DIR + "/.gitignore";
    if (!(await $.fs.exists(ignore))) await $.fs.write(ignore, "*\n");
    for (const candidate of candidates) {
      const path = archivePathFor(candidate.id);
      await $.fs.write(path, candidate.output);
      saved.set(candidate.id, path);
    }
  } catch {
    // An archive that cannot be written only costs the citations; the summary still happens.
  }
  return saved;
}

function instructionsFor(spared: any[], archived: Map<string, string>, candidates: any[]): string {
  const parts: string[] = [];
  if (spared.length > 0) {
    parts.push("The remaining work still depends on the exact output of the earlier tool calls below. Quote each of them VERBATIM in the summary, in full, under a heading that names the call:");
    let budget = VERBATIM_BUDGET_CHARS;
    for (const candidate of spared) {
      if (budget <= 0) break;
      const quoted = candidate.output.slice(0, budget);
      budget -= quoted.length;
      parts.push("--- " + candidate.askedFor.slice(0, CALL_CHARS) + "\n" + quoted);
    }
  }
  if (archived.size > 0) {
    const entries = candidates.filter((c) => archived.has(c.id)).slice(-INDEX_ENTRIES)
      .map((c) => "- " + c.askedFor.slice(0, CALL_CHARS) + " -> " + archived.get(c.id));
    parts.push("Every earlier tool output below is saved in the working directory. Wherever the summary refers to one of these calls, " +
      "include its saved path, and state that the output can be read back from that file and the call must NOT be run again:\n" + entries.join("\n"));
  }
  return parts.join("\n\n");
}

export const register = (on: any) => {
  on("session.compact", async ($: any, event: any, next: any) => {
    // `next` may be called once per compaction: a second call was measured to either skip the compaction
    // silently or throw "reactive compaction did not settle ok".
    let called = false;
    try {
      const url = await $.env.get("ARMADA_MCP_URL");
      const token = await $.env.get("ARMADA_MCP_TOKEN");
      const missionId = await $.env.get("ARMADA_MISSION_ID");
      if (!url || !token || !missionId) {
        called = true;
        return next(event);
      }

      const messages: any[] = event.messages ?? [];
      const candidates = collectCandidates(messages, olderEnd(messages));
      if (candidates.length === 0) {
        called = true;
        return next(event);
      }

      const asked = candidates.slice(0, MAX_ASKED);
      const positions = await askAdmiral($, url, token, missionId, goalOf(messages), asked);
      if (positions === null) {
        called = true;
        return next(event);
      }
      const spared = positions.filter((p) => p >= 0 && p < asked.length).map((p) => asked[p]);
      const archived = await archiveOutputs($, candidates);
      const instructions = instructionsFor(spared, archived, candidates);

      $.ui.log("armada-context-compaction: " + spared.length + " load-bearing result(s) quoted verbatim, " + archived.size +
        " output(s) saved to " + ARCHIVE_DIR + ", harness summarises the rest");
      called = true;
      return next(instructions ? { ...event, instructions } : event);
    } catch (error) {
      $.ui.log("armada-context-compaction: handed back to the harness (" + String(error) + ")");
      if (called) throw error;
      return next(event);
    }
  });
};
