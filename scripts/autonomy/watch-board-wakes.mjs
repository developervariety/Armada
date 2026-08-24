#!/usr/bin/env node
//
// Watch the Armada coordination board over the Admiral's WebSocket hub and
// report directed notes the moment they are posted.
//
// Why this exists: MCP cannot interrupt a running agent. The server has no
// channel to push on, and no client turns an inbound notification into a model
// turn, so Armada delivers a wake by appending it to the next MCP tool result.
// That is reliable but not immediate -- a session that calls no tool sees
// nothing. The WebSocket hub is a real push channel that already broadcasts
// every board message to the dashboard, so this listens there instead and
// surfaces a directed note outside the agent loop: printed, or handed to a
// command of your choosing.
//
// This does not read, acknowledge, or consume anything. It is a notifier, and
// `armada_mark_signal_read` remains the only acknowledgement.
//
// Configuration:
//   ARMADA_WS_URL           Admiral WebSocket URL (default ws://127.0.0.1:7890/ws)
//   ARMADA_PARTICIPANT_KEY  the key whose directed notes you want (required
//                           unless --all)
//   ARMADA_WAKE_COMMAND     optional command run once per matching note; the
//                           note JSON is written to its stdin
//
// Usage:
//   watch-board-wakes.mjs [--all] [--once]

import { spawn } from "node:child_process";

const DEFAULT_WS_URL = "ws://127.0.0.1:7890/ws";
const RECONNECT_MIN_MS = 1000;
const RECONNECT_MAX_MS = 30000;

export function shouldReport(event, participantKey, matchAll) {
  if (!event || event.type !== "coordination.message.created") {
    return false;
  }

  const message = event.data?.message;
  if (!message) {
    return false;
  }
  if (matchAll) {
    return true;
  }

  return typeof message.toParticipantKey === "string"
    && message.toParticipantKey === participantKey;
}

export function formatNote(event) {
  const message = event.data?.message ?? {};
  const room = event.data?.roomKey ?? "fleet";
  const author = message.authorName ?? "unknown";
  const to = message.toParticipantKey ? ` -> ${message.toParticipantKey}` : "";
  const body = (message.content ?? "").replace(/\s+/g, " ").trim();
  return `[board:${room}] ${author}${to}: ${body}`;
}

function runWakeCommand(command, event) {
  // Fire and forget. A failing notifier must never stop the watch.
  try {
    const child = spawn(command, {
      shell: true,
      stdio: ["pipe", "inherit", "inherit"],
    });
    child.on("error", (error) => {
      process.stderr.write(`wake command failed: ${error.message}\n`);
    });
    child.stdin.end(JSON.stringify(event));
  } catch (error) {
    process.stderr.write(`wake command failed: ${error.message}\n`);
  }
}

export async function watch(options = {}) {
  const environment = options.environment || process.env;
  const url = environment.ARMADA_WS_URL || DEFAULT_WS_URL;
  const participantKey = environment.ARMADA_PARTICIPANT_KEY || "";
  const wakeCommand = environment.ARMADA_WAKE_COMMAND || "";
  const matchAll = Boolean(options.all);
  const once = Boolean(options.once);
  const openSocket = options.openSocket || ((target) => new WebSocket(target));

  if (!matchAll && !participantKey) {
    throw new Error("ARMADA_PARTICIPANT_KEY is required unless --all is passed.");
  }

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
        process.stderr.write(`watching ${url} for ${matchAll ? "all notes" : participantKey}\n`);
        socket.send(JSON.stringify({ route: "subscribe" }));
      });

      socket.addEventListener("message", (frame) => {
        let event;
        try {
          event = JSON.parse(typeof frame.data === "string" ? frame.data : String(frame.data));
        } catch {
          return;
        }

        if (!shouldReport(event, participantKey, matchAll)) {
          return;
        }

        process.stdout.write(formatNote(event) + "\n");
        if (wakeCommand) {
          runWakeCommand(wakeCommand, event);
        }
        if (once) {
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

    if (stop) {
      break;
    }

    process.stderr.write(`disconnected (${closed.reason}); retrying in ${backoffMs}ms\n`);
    await new Promise((resolve) => setTimeout(resolve, backoffMs));
    backoffMs = Math.min(backoffMs * 2, RECONNECT_MAX_MS);
  }
}

const isMainModule = process.argv[1] && import.meta.url === `file://${process.argv[1]}`;
if (isMainModule) {
  await watch({
    all: process.argv.includes("--all"),
    once: process.argv.includes("--once"),
  });
}
