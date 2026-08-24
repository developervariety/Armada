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
  assert.equal(describeEvent({ type: "captain.changed", data: { Status: "Working" } }, options()), null);
  assert.equal(describeEvent({ type: "playbook.updated", message: "x" }, options()), null);

  assert.match(
    describeEvent({ type: "captain.changed", data: { Status: "Stalled", Id: "cpt_1", Name: "worker" } }, options()),
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
  listeners.get("message")({ data: JSON.stringify({ type: "mission.changed", data: { id: "msn_1", status: "Complete" } }) });
  listeners.get("message")({ data: JSON.stringify({ type: "voyage.changed", data: { id: "vyg_1", status: "Complete" } }) });
  await done;

  assert.deepEqual(JSON.parse(sent[0]), { route: "subscribe" });
  assert.deepEqual(lines, [
    "mission msn_1 -> Complete",
    "voyage vyg_1 -> Complete",
    "TERMINAL vyg_1 Complete",
  ]);
});
