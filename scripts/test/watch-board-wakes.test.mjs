import assert from "node:assert/strict";
import test from "node:test";

import { formatNote, shouldReport, watch } from "../autonomy/watch-board-wakes.mjs";

const NOTE = (overrides = {}) => ({
  type: "coordination.message.created",
  data: {
    roomKey: "fleet",
    message: {
      authorName: "lead",
      content: "take the vessel review",
      toParticipantKey: "helper-one",
      ...overrides,
    },
  },
});

test("reports only notes addressed to this participant", () => {
  assert.equal(shouldReport(NOTE(), "helper-one", false), true);
  assert.equal(shouldReport(NOTE(), "helper-two", false), false);
  assert.equal(shouldReport(NOTE({ toParticipantKey: null }), "helper-one", false), false);
});

test("--all reports broadcast notes as well", () => {
  assert.equal(shouldReport(NOTE({ toParticipantKey: null }), "", true), true);
  assert.equal(shouldReport(NOTE(), "helper-two", true), true);
});

test("ignores frames that are not board messages", () => {
  assert.equal(shouldReport({ type: "status.snapshot", data: {} }, "helper-one", false), false);
  assert.equal(shouldReport({ type: "coordination.message.created" }, "helper-one", false), false);
  assert.equal(shouldReport(null, "helper-one", false), false);
});

test("formats a note as one readable line", () => {
  assert.equal(
    formatNote(NOTE({ content: "take the\n  vessel   review" })),
    "[board:fleet] lead -> helper-one: take the vessel review",
  );
});

test("requires a participant key unless --all is passed", async () => {
  await assert.rejects(
    () => watch({ environment: {}, openSocket: () => { throw new Error("must not connect"); } }),
    /ARMADA_PARTICIPANT_KEY is required/,
  );
});

test("subscribes on open and reports a matching note", async () => {
  const listeners = new Map();
  const sent = [];
  const fakeSocket = {
    addEventListener: (name, handler) => listeners.set(name, handler),
    send: (payload) => sent.push(payload),
    close: () => listeners.get("close")?.({ code: 1000 }),
  };

  const written = [];
  const originalWrite = process.stdout.write;
  process.stdout.write = (chunk) => { written.push(String(chunk)); return true; };

  try {
    const done = watch({
      environment: { ARMADA_PARTICIPANT_KEY: "helper-one" },
      once: true,
      openSocket: () => fakeSocket,
    });

    listeners.get("open")();
    listeners.get("message")({ data: JSON.stringify({ type: "status.snapshot", data: {} }) });
    listeners.get("message")({ data: JSON.stringify(NOTE({ toParticipantKey: "someone-else" })) });
    listeners.get("message")({ data: JSON.stringify(NOTE()) });
    await done;
  } finally {
    process.stdout.write = originalWrite;
  }

  assert.deepEqual(JSON.parse(sent[0]), { route: "subscribe" });
  assert.equal(written.length, 1, "only the addressed note should be reported");
  assert.match(written[0], /take the vessel review/);
});
