#!/usr/bin/env node
//
// Stream Armada state changes as one line per event, for an operator session to
// consume through its harness's Monitor tool.
//
// WHY THIS EXISTS
//
// The operator loop used to wait by running a blocking poll over SSH: a shell
// `while` loop inside one tool call, several hundred seconds long. That shape
// costs more than it looks. Measured on one session (2026-08-23 23:19Z to
// 01:50Z): 68 assistant messages of which only 5 carried visible text, gaps of
// seven minutes at a time, and three of five turns killed mid-tool-loop. While
// the loop runs the session makes no tool calls, so it cannot see a directed
// board note either -- a helper waiting on an answer times out against a lead
// that is technically alive.
//
// Subscribing is the fix. The Admiral's WebSocket hub already broadcasts every
// voyage, mission, incident and board change. This turns them into lines. Each
// line becomes a notification, the session stays free between events, and a
// stage boundary reaches the operator while the next brief can still be
// corrected.
//
// Configuration:
//   ARMADA_WS_URL           Admiral WebSocket URL (default ws://127.0.0.1:7890/ws)
//   ARMADA_PARTICIPANT_KEY  report board notes addressed to this key
//
// Usage (run ON the Armada server; the hub is loopback-bound):
//   watch-armada.mjs [--voyage <id>] [--participant <key>] [--all-notes]
//                    [--quiet-captains] [--exit-on-terminal]
//
// With --voyage, mission and voyage lines are limited to that voyage and, with
// --exit-on-terminal, the watch ends when the voyage reaches a terminal status.

const DEFAULT_WS_URL = "ws://127.0.0.1:7890/ws";
const RECONNECT_MIN_MS = 1000;
const RECONNECT_MAX_MS = 30000;
const TERMINAL_VOYAGE_STATES = new Set(["Complete", "Failed", "Cancelled"]);

export function parseArguments(argv) {
  const options = {
    voyageId: null,
    participantKey: null,
    allNotes: false,
    quietCaptains: false,
    exitOnTerminal: false,
  };

  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--voyage") options.voyageId = argv[++index] || null;
    else if (argument === "--participant") options.participantKey = argv[++index] || null;
    else if (argument === "--all-notes") options.allNotes = true;
    else if (argument === "--quiet-captains") options.quietCaptains = true;
    else if (argument === "--exit-on-terminal") options.exitOnTerminal = true;
  }

  return options;
}

function shorten(value, limit) {
  const text = String(value ?? "").replace(/\s+/g, " ").trim();
  return text.length > limit ? `${text.slice(0, limit)}...` : text;
}

// Returns a line to emit, or null to stay silent. Silence is the default: every
// line costs the operator a notification, so only changes worth acting on pass.
export function describeEvent(event, options) {
  if (!event || typeof event.type !== "string") return null;
  const data = event.data ?? {};

  switch (event.type) {
    case "voyage.changed": {
      if (options.voyageId && data.id !== options.voyageId) return null;
      return `voyage ${data.id} -> ${data.status}${data.title ? ` (${shorten(data.title, 60)})` : ""}`;
    }

    case "mission.changed": {
      // A mission line is the stage boundary, which is the only window in which a
      // correction can still reach the next brief.
      if (options.voyageId && data.voyageId && data.voyageId !== options.voyageId) return null;
      return `mission ${data.id} -> ${data.status}${data.title ? ` (${shorten(data.title, 60)})` : ""}`;
    }

    case "incident.changed": {
      const severity = data.Severity ?? data.severity ?? "";
      const status = data.Status ?? data.status ?? "";
      const id = data.Id ?? data.id ?? "";
      const title = data.Title ?? data.title ?? "";
      return `INCIDENT ${id} ${status}${severity ? ` sev=${severity}` : ""}${title ? ` (${shorten(title, 60)})` : ""}`;
    }

    case "captain.changed": {
      if (options.quietCaptains) return null;
      const status = data.Status ?? data.status ?? "";
      if (status !== "Stalled") return null; // only a stall is actionable
      return `CAPTAIN STALLED ${data.Id ?? data.id ?? ""} ${data.Name ?? data.name ?? ""}`;
    }

    case "coordination.message.created": {
      const message = data.message;
      if (!message) return null;
      const addressed = message.toParticipantKey;
      const mine = options.participantKey && addressed === options.participantKey;
      if (!mine && !options.allNotes) return null;
      const marker = mine ? "MAIL" : "board";
      return `${marker} <${message.authorName ?? "unknown"}> ${shorten(message.content, 240)}`;
    }

    default: {
      // Fleet events arrive through the generic envelope with a message string.
      // Report only the ones an operator would act on; the rest are noise.
      const actionable = [
        "mission.failed",
        "voyage.cancelled",
        "autonomous_recovery.incident_opened",
        "autonomous_recovery.rescue_dispatched",
        "autonomous_recovery.blocked",
      ];
      if (!actionable.includes(event.type)) return null;
      return `${event.type}: ${shorten(event.message, 200)}`;
    }
  }
}

export function isTerminalVoyageEvent(event, options) {
  if (!options.voyageId || !options.exitOnTerminal) return false;
  if (event?.type !== "voyage.changed") return false;
  if (event.data?.id !== options.voyageId) return false;
  return TERMINAL_VOYAGE_STATES.has(event.data?.status);
}

export async function watch(options = {}) {
  const environment = options.environment || process.env;
  const url = environment.ARMADA_WS_URL || DEFAULT_WS_URL;
  const settings = {
    voyageId: options.voyageId ?? null,
    participantKey: options.participantKey ?? environment.ARMADA_PARTICIPANT_KEY ?? null,
    allNotes: Boolean(options.allNotes),
    quietCaptains: Boolean(options.quietCaptains),
    exitOnTerminal: Boolean(options.exitOnTerminal),
  };
  const openSocket = options.openSocket || ((target) => new WebSocket(target));
  const write = options.write || ((line) => process.stdout.write(`${line}\n`));
  const note = options.note || ((line) => process.stderr.write(`${line}\n`));

  let backoffMs = RECONNECT_MIN_MS;
  let stop = false;

  while (!stop) {
    const closed = await new Promise((resolve) => {
      let socket;
      try {
        socket = openSocket(url);
      } catch (error) {
        resolve({ reason: error.message });
        return;
      }

      socket.addEventListener("open", () => {
        backoffMs = RECONNECT_MIN_MS;
        note(`watching ${url}${settings.voyageId ? ` voyage=${settings.voyageId}` : ""}` +
          `${settings.participantKey ? ` mail=${settings.participantKey}` : ""}`);
        socket.send(JSON.stringify({ route: "subscribe" }));
      });

      socket.addEventListener("message", (frame) => {
        let event;
        try {
          event = JSON.parse(typeof frame.data === "string" ? frame.data : String(frame.data));
        } catch {
          return;
        }

        const line = describeEvent(event, settings);
        if (line) write(line);

        if (isTerminalVoyageEvent(event, settings)) {
          write(`TERMINAL ${event.data.id} ${event.data.status}`);
          stop = true;
          try { socket.close(); } catch { /* already closing */ }
        }
      });

      socket.addEventListener("error", () => {
        // The close event that follows carries the retry decision.
      });

      socket.addEventListener("close", (event) => {
        resolve({ reason: `code ${event.code}` });
      });
    });

    if (stop) break;

    // A dropped socket is not the end of the watch. Reconnecting silently would
    // hide a hub that is refusing connections, so say so and back off.
    note(`disconnected (${closed.reason}); retrying in ${backoffMs}ms`);
    await new Promise((resolve) => setTimeout(resolve, backoffMs));
    backoffMs = Math.min(backoffMs * 2, RECONNECT_MAX_MS);
  }
}

const isMainModule = process.argv[1] && import.meta.url === `file://${process.argv[1]}`;
if (isMainModule) {
  await watch(parseArguments(process.argv.slice(2)));
}
