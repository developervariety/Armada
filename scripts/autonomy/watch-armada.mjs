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
//   ARMADA_API_KEY          admiral API key sent in the authenticate frame
//   ARMADA_TOKEN            bearer credential token; used instead of the API key
//
// The hub refuses a session that does not authenticate. A refusal ends the watch
// with a hint, because reconnecting with the same credentials cannot succeed.
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
      if (options.voyageId && data.voyageId !== options.voyageId) return null;
      return `mission ${data.id} -> ${data.status}${data.title ? ` (${shorten(data.title, 60)})` : ""}`;
    }

    case "check-run.changed": {
      if (options.voyageId && data.voyageId !== options.voyageId) return null;
      const label = data.label || data.type || "Check";
      const queue = Number.isFinite(data.queueDurationMs) ? ` queue=${data.queueDurationMs}ms` : "";
      const run = Number.isFinite(data.durationMs) ? ` run=${data.durationMs}ms` : "";
      return `check ${data.id} ${label} -> ${data.status}${queue}${run}`;
    }

    case "event.gap": {
      const reason = shorten(data.reason || event.message || "event history is not available", 160);
      return `EVENT GAP${reason ? `: ${reason}` : ""}`;
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
      const state = data.State ?? data.state ?? data.Status ?? data.status ?? "";
      if (state !== "Stalled") return null; // only a stall is actionable
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
    apiKey: environment.ARMADA_API_KEY || null,
    token: environment.ARMADA_TOKEN || null,
    allNotes: Boolean(options.allNotes),
    quietCaptains: Boolean(options.quietCaptains),
    exitOnTerminal: Boolean(options.exitOnTerminal),
  };
  const openSocket = options.openSocket || ((target) => new WebSocket(target));
  const write = options.write || ((line) => process.stdout.write(`${line}\n`));
  const note = options.note || ((line) => process.stderr.write(`${line}\n`));
  const wait = options.wait || ((durationMs) => new Promise((resolve) => setTimeout(resolve, durationMs)));

  let backoffMs = RECONNECT_MIN_MS;
  let stop = false;
  let streamId = null;
  let cursor = null;
  const knownStates = new Map();

  const eventState = (event) => {
    const data = event?.data ?? {};
    switch (event?.type) {
      case "voyage.changed": return { key: `voyage:${data.id}`, state: data.status };
      case "mission.changed": return { key: `mission:${data.id}`, state: data.status };
      case "captain.changed": return { key: `captain:${data.Id ?? data.id}`, state: data.State ?? data.state ?? data.Status ?? data.status };
      case "check-run.changed": return { key: `check:${data.Id ?? data.id}`, state: data.Status ?? data.status };
      default: return null;
    }
  };

  const remember = (event) => {
    const current = eventState(event);
    if (!current?.key || current.key.endsWith(":")) return false;
    const changed = knownStates.has(current.key) && knownStates.get(current.key) !== current.state;
    knownStates.set(current.key, current.state);
    return changed;
  };

  const snapshotEvents = (reconciliation) => {
    const result = [];
    for (const voyage of reconciliation?.voyages ?? [])
      result.push({ type: "voyage.changed", data: voyage });
    for (const mission of reconciliation?.missions ?? [])
      result.push({ type: "mission.changed", data: mission });
    for (const captain of reconciliation?.captains ?? [])
      result.push({ type: "captain.changed", data: captain });
    for (const checkRun of reconciliation?.checkRuns ?? [])
      result.push({ type: "check-run.changed", data: checkRun });
    return result;
  };

  const isActionableSnapshotState = (event) => {
    const state = event?.data?.status ?? event?.data?.state;
    if (event?.type === "mission.changed")
      return ["Failed", "LandingFailed", "WaitingForInput"].includes(state);
    if (event?.type === "captain.changed") return state === "Stalled";
    if (event?.type === "check-run.changed") return state === "Failed";
    return false;
  };

  const applySnapshot = (event, socket) => {
    const reconciliation = event?.data?.reconciliation;
    if (!reconciliation) return false;

    const events = snapshotEvents(reconciliation);
    let terminal = null;
    for (const reconciledEvent of events) {
      const changed = remember(reconciledEvent);
      if (changed || isActionableSnapshotState(reconciledEvent)) {
        const line = describeEvent(reconciledEvent, settings);
        if (line) write(line);
      }
      if (isTerminalVoyageEvent(reconciledEvent, settings)) terminal = reconciledEvent;
    }

    write(`RECONCILED voyages=${reconciliation.voyages?.length ?? 0}` +
      ` missions=${reconciliation.missions?.length ?? 0}` +
      ` captains=${reconciliation.captains?.length ?? 0}` +
      ` checks=${reconciliation.checkRuns?.length ?? 0}`);

    if (terminal) {
      write(`TERMINAL ${terminal.data.id} ${terminal.data.status}`);
      stop = true;
      try { socket.close(); } catch { /* already closing */ }
      return true;
    }
    return false;
  };

  while (!stop) {
    const closed = await new Promise((resolve) => {
      let socket;
      let snapshotApplied = false;
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
        if (settings.apiKey || settings.token) {
          const authenticate = { route: "authenticate" };
          if (settings.token) authenticate.token = settings.token;
          else authenticate.apiKey = settings.apiKey;
          socket.send(JSON.stringify(authenticate));
        }
        const subscription = { route: "subscribe" };
        if (streamId) subscription.streamId = streamId;
        if (cursor !== null) subscription.cursor = cursor;
        if (settings.voyageId) subscription.voyageId = settings.voyageId;
        socket.send(JSON.stringify(subscription));
      });

      socket.addEventListener("message", (frame) => {
        let event;
        try {
          event = JSON.parse(typeof frame.data === "string" ? frame.data : String(frame.data));
        } catch {
          return;
        }

        if (event.type === "auth.result") return;

        if (event.type === "auth.required" || event.type === "auth.failed") {
          // Retrying with the same credentials cannot succeed, so stop instead of
          // reconnecting forever.
          note(`AUTH ${event.type}: ${shorten(event.message, 200)}; set ARMADA_API_KEY or ARMADA_TOKEN`);
          stop = true;
          try { socket.close(); } catch { /* already closing */ }
          return;
        }

        if (event.type === "status.snapshot") {
          if (typeof event.streamId === "string" && event.streamId) streamId = event.streamId;
          if (Number.isSafeInteger(event.cursor)) cursor = event.cursor;
          snapshotApplied = Boolean(event?.data?.reconciliation);
          applySnapshot(event, socket);
          return;
        }

        if (event.type === "stream.ready") {
          const validBoundary = snapshotApplied
            && typeof event.streamId === "string"
            && event.streamId === streamId
            && Number.isSafeInteger(event.cursor)
            && event.cursor === cursor;
          if (!validBoundary) {
            note(`EVENT GAP invalid stream.ready boundary; reconnecting`);
            try { socket.close(); } catch { /* close event will schedule retry */ }
          }
          return;
        }

        if (event.type === "event.gap") {
          const line = describeEvent(event, settings);
          if (line) write(line);
          return;
        }

        const sequenced = typeof event.streamId === "string" && event.streamId && Number.isSafeInteger(event.cursor);
        if (sequenced && cursor !== null) {
          const expected = cursor + 1;
          if (event.streamId !== streamId || event.cursor !== expected) {
            note(`EVENT GAP expected=${streamId ?? "unknown"}/${expected} received=${event.streamId}/${event.cursor}; reconnecting`);
            try { socket.close(); } catch { /* close event will schedule retry */ }
            return;
          }
        }

        if (sequenced) {
          streamId = event.streamId;
          cursor = event.cursor;
        }

        const line = describeEvent(event, settings);
        if (line) write(line);

        remember(event);

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
    await wait(backoffMs);
    backoffMs = Math.min(backoffMs * 2, RECONNECT_MAX_MS);
  }
}

const isMainModule = process.argv[1] && import.meta.url === `file://${process.argv[1]}`;
if (isMainModule) {
  await watch(parseArguments(process.argv.slice(2)));
}
