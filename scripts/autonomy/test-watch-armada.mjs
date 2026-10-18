#!/usr/bin/env node
// Self-check for watch-armada.mjs: a refused subscription is never silent.
//
// Run:  node scripts/autonomy/test-watch-armada.mjs
// Exit: 0 when every check passes, 1 otherwise. No network: every socket is an injected fake.
//
// The guarantees it proves:
//   1. A command.error or subscribe.error frame that answers the watch's subscribe is printed,
//      and the watch reconnects and goes on to deliver the stream.
//   2. Repeated refused subscriptions stop the watch with a STOPPED line and exit code 1.

import { watch, MAX_SUBSCRIBE_FAILURES } from "./watch-armada.mjs";

let failures = 0;
function check(label, condition, detail) {
  if (condition) {
    console.log(`PASS  ${label}`);
  } else {
    failures += 1;
    console.log(`FAIL  ${label}${detail ? `: ${detail}` : ""}`);
  }
}

// A fake socket: it opens on the next tick and answers the subscribe frame with `reply`.
function fakeSocket(reply) {
  const listeners = new Map();
  let closed = false;
  const socket = {
    addEventListener(type, handler) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(handler);
    },
    emit(type, event) {
      for (const handler of listeners.get(type) ?? []) handler(event);
    },
    send(text) {
      const frame = JSON.parse(text);
      if (frame.route !== "subscribe") return;
      setTimeout(() => {
        for (const message of reply) {
          if (closed) return;
          socket.emit("message", { data: JSON.stringify(message) });
        }
      }, 0);
    },
    close() {
      if (closed) return;
      closed = true;
      setTimeout(() => socket.emit("close", { code: 1000 }), 0);
    },
  };
  setTimeout(() => socket.emit("open", {}), 0);
  return socket;
}

const terminalSnapshot = {
  type: "status.snapshot",
  streamId: "stream-example",
  cursor: 1,
  data: { reconciliation: { voyages: [{ id: "vyg_example", status: "Complete" }], missions: [], captains: [], checkRuns: [] } },
};

async function run(replies) {
  const lines = [];
  let opened = 0;
  const exitCode = await watch({
    voyageId: "vyg_example",
    exitOnTerminal: true,
    environment: {},
    openSocket: () => fakeSocket(replies[Math.min(opened++, replies.length - 1)]),
    write: (line) => lines.push(line),
    note: () => {},
    wait: () => Promise.resolve(),
  });
  return { exitCode, lines, opened };
}

{
  const refusal = { type: "command.error", error: "23503: example foreign key failure" };
  const result = await run([[refusal], [terminalSnapshot]]);
  check("a refused subscribe is printed", result.lines.some((line) => line.startsWith("HUB command.error: 23503")), result.lines.join(" | "));
  check("the watch reconnects after a refused subscribe", result.opened === 2, `opened ${result.opened}`);
  check("the reconnected watch delivers the stream", result.lines.includes("TERMINAL vyg_example Complete"), result.lines.join(" | "));
  check("a watch that recovers exits 0", result.exitCode === 0, `exit ${result.exitCode}`);
}

{
  const refusal = { type: "subscribe.error", error: "snapshot failed", retryable: true };
  const result = await run([[refusal]]);
  const printed = result.lines.filter((line) => line.startsWith("HUB subscribe.error: snapshot failed")).length;
  check("every refused subscribe is printed", printed === MAX_SUBSCRIBE_FAILURES, `printed ${printed}`);
  check("repeated refusals stop the watch with a reason", result.lines.some((line) => line.startsWith("STOPPED: subscribe refused")), result.lines.join(" | "));
  check("repeated refusals exit non-zero", result.exitCode === 1, `exit ${result.exitCode}`);
}

console.log(failures === 0 ? "RESULT: PASS" : `RESULT: FAIL (${failures})`);
process.exitCode = failures === 0 ? 0 : 1;
