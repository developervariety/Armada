import assert from "node:assert/strict";
import test from "node:test";

import {
  describeEvent,
  isTerminalVoyageEvent,
  parseArguments,
  watch,
} from "../autonomy/watch-armada.mjs";

const BASE = { voyageId: null, participantKey: null, allNotes: false, quietCaptains: false, exitOnTerminal: false };
const options = (overrides = {}) => ({ ...BASE, ...overrides });

test("parses the command line", () => {
  const parsed = parseArguments(["--voyage", "vyg_1", "--participant", "lead", "--exit-on-terminal"]);
  assert.equal(parsed.voyageId, "vyg_1");
  assert.equal(parsed.participantKey, "lead");
  assert.equal(parsed.exitOnTerminal, true);
  assert.equal(parsed.allNotes, false);
});

test("reports voyage and mission transitions, which are the correction window", () => {
  assert.match(
    describeEvent({ type: "voyage.changed", data: { id: "vyg_1", status: "InProgress" } }, options()),
    /^voyage vyg_1 -> InProgress$/,
  );
  assert.match(
    describeEvent({ type: "mission.changed", data: { id: "msn_1", status: "Complete", title: "Judge" } }, options()),
    /^mission msn_1 -> Complete \(Judge\)$/,
  );
});

test("a voyage filter excludes other voyages", () => {
  const only = options({ voyageId: "vyg_1" });
  assert.equal(describeEvent({ type: "voyage.changed", data: { id: "vyg_2", status: "Failed" } }, only), null);
  assert.ok(describeEvent({ type: "voyage.changed", data: { id: "vyg_1", status: "Failed" } }, only));
  assert.equal(
    describeEvent({ type: "mission.changed", data: { id: "msn_9", status: "Complete", voyageId: "vyg_2" } }, only),
    null,
  );
  assert.equal(
    describeEvent({ type: "mission.changed", data: { id: "msn_missing", status: "Complete" } }, only),
    null,
  );
});

test("reports Check lifecycle timing and applies the voyage filter", () => {
  const event = {
    type: "check-run.changed",
    data: { id: "chk_1", voyageId: "vyg_1", type: "UnitTest", status: "Passed", queueDurationMs: 25, durationMs: 90 },
  };
  assert.equal(describeEvent(event, options()), "check chk_1 UnitTest -> Passed queue=25ms run=90ms");
  assert.ok(describeEvent(event, options({ voyageId: "vyg_1" })));
  assert.equal(describeEvent(event, options({ voyageId: "vyg_2" })), null);
});

test("directed mail is reported, other sessions' notes are not", () => {
  const mine = options({ participantKey: "lead" });
  const note = (to) => ({
    type: "coordination.message.created",
    data: { roomKey: "fleet", message: { authorName: "helper", content: "answer ready", toParticipantKey: to } },
  });
  assert.match(describeEvent(note("lead"), mine), /^MAIL <helper> answer ready$/);
  assert.equal(describeEvent(note("someone-else"), mine), null);
  assert.equal(describeEvent(note(null), mine), null);
  assert.match(describeEvent(note(null), options({ allNotes: true })), /^board <helper>/);
});

test("stays silent on routine noise but reports what needs action", () => {
  assert.equal(describeEvent({ type: "status.snapshot", data: {} }, options()), null);
  assert.equal(describeEvent({ type: "captain.changed", data: { state: "Working" } }, options()), null);
  assert.equal(describeEvent({ type: "playbook.updated", message: "x" }, options()), null);

  assert.match(
    describeEvent({ type: "captain.changed", data: { state: "Stalled", id: "cpt_1", name: "worker" } }, options()),
    /^CAPTAIN STALLED cpt_1 worker$/,
  );
  assert.match(
    describeEvent({ type: "incident.changed", data: { Id: "inc_1", Status: "Open", Severity: "High" } }, options()),
    /^INCIDENT inc_1 Open sev=High$/,
  );
  assert.match(
    describeEvent({ type: "mission.failed", message: "gate red" }, options()),
    /^mission\.failed: gate red$/,
  );
  assert.equal(
    describeEvent({ type: "event.gap", data: { reason: "cursor expired" } }, options()),
    "EVENT GAP: cursor expired",
  );
});

test("collapses whitespace so one event stays one line", () => {
  const line = describeEvent({
    type: "coordination.message.created",
    data: { message: { authorName: "helper", content: "line one\nline  two", toParticipantKey: "lead" } },
  }, options({ participantKey: "lead" }));
  assert.equal(line.includes("\n"), false);
  assert.match(line, /line one line two/);
});

test("ends the watch when the tracked voyage reaches a terminal status", () => {
  const only = options({ voyageId: "vyg_1", exitOnTerminal: true });
  assert.equal(isTerminalVoyageEvent({ type: "voyage.changed", data: { id: "vyg_1", status: "Complete" } }, only), true);
  assert.equal(isTerminalVoyageEvent({ type: "voyage.changed", data: { id: "vyg_1", status: "InProgress" } }, only), false);
  assert.equal(isTerminalVoyageEvent({ type: "voyage.changed", data: { id: "vyg_2", status: "Complete" } }, only), false);
  // Without the flag the watch continues, so a long campaign keeps one monitor.
  assert.equal(
    isTerminalVoyageEvent({ type: "voyage.changed", data: { id: "vyg_1", status: "Complete" } }, options({ voyageId: "vyg_1" })),
    false,
  );
});

test("subscribes on open and stops on the terminal voyage event", async () => {
  const listeners = new Map();
  const sent = [];
  const lines = [];
  const socket = {
    addEventListener: (name, handler) => listeners.set(name, handler),
    send: (payload) => sent.push(payload),
    close: () => listeners.get("close")?.({ code: 1000 }),
  };

  const done = watch({
    environment: {},
    voyageId: "vyg_1",
    exitOnTerminal: true,
    openSocket: () => socket,
    write: (line) => lines.push(line),
    note: () => {},
  });

  listeners.get("open")();
  listeners.get("message")({ data: JSON.stringify({ type: "status.snapshot", data: {} }) });
  listeners.get("message")({ data: JSON.stringify({ type: "mission.changed", data: { id: "msn_1", voyageId: "vyg_1", status: "Complete" } }) });
  listeners.get("message")({ data: JSON.stringify({ type: "voyage.changed", data: { id: "vyg_1", status: "Complete" } }) });
  await done;

  assert.deepEqual(JSON.parse(sent[0]), { route: "subscribe", voyageId: "vyg_1" });
  assert.deepEqual(lines, [
    "mission msn_1 -> Complete",
    "voyage vyg_1 -> Complete",
    "TERMINAL vyg_1 Complete",
  ]);
});

test("authenticates before subscribing when an API key is configured", async () => {
  const listeners = new Map();
  const sent = [];
  const socket = {
    addEventListener: (name, handler) => listeners.set(name, handler),
    send: (payload) => sent.push(payload),
    close: () => listeners.get("close")?.({ code: 1000 }),
  };

  const done = watch({
    environment: { ARMADA_API_KEY: "key-1" },
    voyageId: "vyg_1",
    exitOnTerminal: true,
    openSocket: () => socket,
    write: () => {},
    note: () => {},
  });

  listeners.get("open")();
  listeners.get("message")({ data: JSON.stringify({ type: "auth.result", data: { authenticated: true } }) });
  listeners.get("message")({ data: JSON.stringify({ type: "voyage.changed", data: { id: "vyg_1", status: "Complete" } }) });
  await done;

  assert.deepEqual(JSON.parse(sent[0]), { route: "authenticate", apiKey: "key-1" });
  assert.deepEqual(JSON.parse(sent[1]), { route: "subscribe", voyageId: "vyg_1" });
});

test("a bearer token is sent instead of the API key when both are configured", async () => {
  const listeners = new Map();
  const sent = [];
  const socket = {
    addEventListener: (name, handler) => listeners.set(name, handler),
    send: (payload) => sent.push(payload),
    close: () => listeners.get("close")?.({ code: 1000 }),
  };

  const done = watch({
    environment: { ARMADA_API_KEY: "key-1", ARMADA_TOKEN: "token-1" },
    voyageId: "vyg_1",
    exitOnTerminal: true,
    openSocket: () => socket,
    write: () => {},
    note: () => {},
  });

  listeners.get("open")();
  listeners.get("message")({ data: JSON.stringify({ type: "voyage.changed", data: { id: "vyg_1", status: "Failed" } }) });
  await done;

  assert.deepEqual(JSON.parse(sent[0]), { route: "authenticate", token: "token-1" });
});

test("an authentication refusal stops the watch instead of reconnecting", async () => {
  let opened = 0;
  const notes = [];
  const makeSocket = () => {
    opened += 1;
    const listeners = new Map();
    const socket = {
      addEventListener: (name, handler) => listeners.set(name, handler),
      send: () => {},
      close: () => listeners.get("close")?.({ code: 1008 }),
    };
    queueMicrotask(() => {
      listeners.get("open")();
      listeners.get("message")({ data: JSON.stringify({ type: "auth.required", message: "Authenticate this session" }) });
      listeners.get("close")?.({ code: 1008 });
    });
    return socket;
  };

  await watch({
    environment: {},
    openSocket: makeSocket,
    write: () => {},
    note: (line) => notes.push(line),
    wait: async () => {},
  });

  assert.equal(opened, 1);
  assert.ok(notes.some((line) => /ARMADA_API_KEY/.test(line)), `expected a credential hint, got ${JSON.stringify(notes)}`);
});

test("reconnects with the last cursor and tracked voyage", async () => {
  const sockets = [];
  const makeSocket = () => {
    const listeners = new Map();
    const sent = [];
    const socket = {
      listeners,
      sent,
      addEventListener: (name, handler) => listeners.set(name, handler),
      send: (payload) => sent.push(payload),
      close: () => listeners.get("close")?.({ code: 1000 }),
    };
    sockets.push(socket);
    return socket;
  };

  const done = watch({
    environment: {},
    voyageId: "vyg_1",
    exitOnTerminal: true,
    openSocket: makeSocket,
    write: () => {},
    note: () => {},
    wait: async () => {},
  });

  sockets[0].listeners.get("open")();
  sockets[0].listeners.get("message")({ data: JSON.stringify({
    type: "mission.changed", streamId: "stream-a", cursor: 7,
    data: { id: "msn_1", voyageId: "vyg_1", status: "InProgress" },
  }) });
  sockets[0].close();
  await new Promise((resolve) => setImmediate(resolve));

  sockets[1].listeners.get("open")();
  assert.deepEqual(JSON.parse(sockets[1].sent[0]), {
    route: "subscribe", streamId: "stream-a", cursor: 7, voyageId: "vyg_1",
  });
  sockets[1].listeners.get("message")({ data: JSON.stringify({
    type: "voyage.changed", streamId: "stream-a", cursor: 8,
    data: { id: "vyg_1", status: "Complete" },
  }) });
  await done;
});

test("a local cursor discontinuity closes and resumes from the last good cursor", async () => {
  const sockets = [];
  const notes = [];
  const makeSocket = () => {
    const listeners = new Map();
    const sent = [];
    const socket = {
      listeners,
      sent,
      addEventListener: (name, handler) => listeners.set(name, handler),
      send: (payload) => sent.push(payload),
      close: () => listeners.get("close")?.({ code: 1000 }),
    };
    sockets.push(socket);
    return socket;
  };

  const done = watch({
    environment: {},
    voyageId: "vyg_1",
    exitOnTerminal: true,
    openSocket: makeSocket,
    write: () => {},
    note: (line) => notes.push(line),
    wait: async () => {},
  });

  sockets[0].listeners.get("open")();
  sockets[0].listeners.get("message")({ data: JSON.stringify({
    type: "mission.changed", streamId: "stream-a", cursor: 4,
    data: { id: "msn_1", voyageId: "vyg_1", status: "InProgress" },
  }) });
  sockets[0].listeners.get("message")({ data: JSON.stringify({
    type: "mission.changed", streamId: "stream-a", cursor: 6,
    data: { id: "msn_1", voyageId: "vyg_1", status: "Complete" },
  }) });
  await new Promise((resolve) => setImmediate(resolve));

  assert.match(notes.find((line) => line.startsWith("EVENT GAP")), /expected=stream-a\/5 received=stream-a\/6/);
  sockets[1].listeners.get("open")();
  assert.deepEqual(JSON.parse(sockets[1].sent[0]), {
    route: "subscribe", streamId: "stream-a", cursor: 4, voyageId: "vyg_1",
  });
  sockets[1].listeners.get("message")({ data: JSON.stringify({
    type: "voyage.changed", streamId: "stream-a", cursor: 5,
    data: { id: "vyg_1", status: "Failed" },
  }) });
  await done;
});

test("an authoritative snapshot reconciles state and detects a terminal tracked voyage", async () => {
  const listeners = new Map();
  const lines = [];
  const socket = {
    addEventListener: (name, handler) => listeners.set(name, handler),
    send: () => {},
    close: () => listeners.get("close")?.({ code: 1000 }),
  };

  const done = watch({
    environment: {},
    voyageId: "vyg_1",
    exitOnTerminal: true,
    openSocket: () => socket,
    write: (line) => lines.push(line),
    note: () => {},
  });

  listeners.get("open")();
  listeners.get("message")({ data: JSON.stringify({
    type: "status.snapshot",
    streamId: "stream-new",
    cursor: 21,
    data: {
      status: { activeVoyages: 0 },
      reconciliation: {
        voyages: [{ id: "vyg_1", status: "Complete", title: "done" }],
        missions: [{ id: "msn_1", voyageId: "vyg_1", status: "Failed" }],
        captains: [{ id: "cpt_1", name: "worker", state: "Stalled" }],
        checkRuns: [{ id: "chk_1", voyageId: "vyg_1", type: "UnitTest", status: "Failed" }],
      },
    },
  }) });
  await done;

  assert.deepEqual(lines, [
    "mission msn_1 -> Failed",
    "CAPTAIN STALLED cpt_1 worker",
    "check chk_1 UnitTest -> Failed",
    "RECONCILED voyages=1 missions=1 captains=1 checks=1",
    "TERMINAL vyg_1 Complete",
  ]);
});

test("an explicit server gap is reported before reconciliation", async () => {
  const listeners = new Map();
  const lines = [];
  const socket = {
    addEventListener: (name, handler) => listeners.set(name, handler),
    send: () => {},
    close: () => listeners.get("close")?.({ code: 1000 }),
  };

  const done = watch({
    environment: {},
    voyageId: "vyg_1",
    exitOnTerminal: true,
    openSocket: () => socket,
    write: (line) => lines.push(line),
    note: () => {},
  });

  listeners.get("open")();
  listeners.get("message")({ data: JSON.stringify({
    type: "event.gap", streamId: "stream-b", cursor: 30, data: { reason: "cursor expired" },
  }) });
  listeners.get("message")({ data: JSON.stringify({
    type: "status.snapshot", streamId: "stream-b", cursor: 30,
    data: { status: {}, reconciliation: {
      voyages: [{ id: "vyg_1", status: "Cancelled" }], missions: [], captains: [], checkRuns: [],
    } },
  }) });
  await done;

  assert.deepEqual(lines, [
    "EVENT GAP: cursor expired",
    "RECONCILED voyages=1 missions=0 captains=0 checks=0",
    "TERMINAL vyg_1 Cancelled",
  ]);
});

test("a gap control frame never advances the reconnect cursor", async () => {
  const sockets = [];
  const makeSocket = () => {
    const listeners = new Map();
    const sent = [];
    const socket = {
      listeners,
      sent,
      addEventListener: (name, handler) => listeners.set(name, handler),
      send: (payload) => sent.push(payload),
      close: () => listeners.get("close")?.({ code: 1000 }),
    };
    sockets.push(socket);
    return socket;
  };

  const done = watch({
    environment: {}, voyageId: "vyg_1", exitOnTerminal: true,
    openSocket: makeSocket, write: () => {}, note: () => {}, wait: async () => {},
  });

  sockets[0].listeners.get("open")();
  sockets[0].listeners.get("message")({ data: JSON.stringify({
    type: "status.snapshot", streamId: "stream-a", cursor: 5,
    data: { reconciliation: { voyages: [], missions: [], captains: [], checkRuns: [] } },
  }) });
  sockets[0].listeners.get("message")({ data: JSON.stringify({
    type: "stream.ready", streamId: "stream-a", cursor: 5,
  }) });
  sockets[0].listeners.get("message")({ data: JSON.stringify({
    type: "event.gap", streamId: "stream-a", data: { reason: "snapshot_overflow", currentCursor: 200 },
  }) });
  sockets[0].close();
  await new Promise((resolve) => setImmediate(resolve));

  sockets[1].listeners.get("open")();
  assert.deepEqual(JSON.parse(sockets[1].sent[0]), {
    route: "subscribe", streamId: "stream-a", cursor: 5, voyageId: "vyg_1",
  });
  sockets[1].listeners.get("message")({ data: JSON.stringify({
    type: "status.snapshot", streamId: "stream-a", cursor: 200,
    data: { reconciliation: {
      voyages: [{ id: "vyg_1", status: "Complete" }], missions: [], captains: [], checkRuns: [],
    } },
  }) });
  await done;
});
