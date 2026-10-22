// Behaviour tests for the MCP SSH bridge. Run with: node --test scripts/mcp-ssh-http-bridge.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

import { resolveParticipantIdentity } from "./mcp-ssh-http-bridge.mjs";

const bridgePath = join(dirname(fileURLToPath(import.meta.url)), "mcp-ssh-http-bridge.mjs");

// A stand-in for ssh: it records the remote command it was given and answers with
// one HTTP response, so a test sees exactly the headers a real request would send.
function createFakeSsh(directory) {
  const logPath = join(directory, "remote-commands.log");
  const scriptPath = join(directory, "fake-ssh.sh");
  writeFileSync(scriptPath, [
    "#!/bin/sh",
    "for last; do :; done",
    `printf '%s\\n' "$last" >> '${logPath}'`,
    "cat > /dev/null",
    "printf 'HTTP/1.1 200 OK\\r\\nContent-Type: application/json\\r\\n\\r\\n{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'",
    "",
  ].join("\n"));
  chmodSync(scriptPath, 0o755);
  return { scriptPath, logPath };
}

// Run one bridge process, send it two tool calls and wait for both answers.
async function runBridgeProcess(fakeSsh, extraEnvironment) {
  const child = spawn(process.execPath, [bridgePath], {
    stdio: ["pipe", "pipe", "pipe"],
    env: {
      PATH: process.env.PATH,
      ARMADA_SSH_COMMAND: fakeSsh.scriptPath,
      ARMADA_MCP_AUTH_HEADER_FILE: "/srv/example/mcp-auth-header",
      ...extraEnvironment,
    },
  });

  let output = "";
  const answered = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("bridge did not answer two calls within 10 s")), 10000);
    child.stdout.on("data", (chunk) => {
      output += chunk.toString("utf8");
      if (output.split("\n").filter((line) => line.trim().length > 0).length >= 2) {
        clearTimeout(timer);
        resolve();
      }
    });
    child.on("error", reject);
  });

  for (const id of [1, 2]) {
    child.stdin.write(JSON.stringify({
      jsonrpc: "2.0",
      id,
      method: "tools/call",
      params: { name: "armada_status", arguments: {} },
    }) + "\n");
  }

  await answered;
  child.stdin.end();
  await new Promise((resolve) => child.on("close", resolve));
}

function participantHeaders(logPath) {
  return readFileSync(logPath, "utf8")
    .split("\n")
    .filter((line) => line.length > 0)
    .map((line) => ({
      key: /X-Armada-Participant: ([A-Za-z0-9._:-]+)/.exec(line)?.[1] ?? null,
      baseKey: /X-Armada-Participant-Base: ([A-Za-z0-9._:-]+)/.exec(line)?.[1] ?? null,
    }));
}

test("two bridge processes on one configuration get different participant keys, each kept for its own life", async () => {
  const directory = mkdtempSync(join(tmpdir(), "bridge-key-"));
  try {
    const fakeSsh = createFakeSsh(directory);
    const environment = { ARMADA_PARTICIPANT_KEY: "operator", ARMADA_PARTICIPANT_KEY_UNIQUE: "1" };

    await runBridgeProcess(fakeSsh, environment);
    await runBridgeProcess(fakeSsh, environment);

    const sent = participantHeaders(fakeSsh.logPath);
    assert.equal(sent.length, 4, "each process sent two requests");
    for (const request of sent) {
      assert.match(request.key ?? "", /^operator-[0-9a-z]{6}$/, "the key is the configured key plus a suffix");
      assert.equal(request.baseKey, "operator", "the configured key travels as the base key");
    }
    assert.equal(sent[0].key, sent[1].key, "one process keeps one key for every request");
    assert.equal(sent[2].key, sent[3].key, "one process keeps one key for every request");
    assert.notEqual(sent[0].key, sent[2].key, "two processes do not share a key");
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("without the unique flag the configured key is sent unchanged and no base key is sent", async () => {
  const directory = mkdtempSync(join(tmpdir(), "bridge-key-"));
  try {
    const fakeSsh = createFakeSsh(directory);

    await runBridgeProcess(fakeSsh, { ARMADA_PARTICIPANT_KEY: "operator" });

    for (const request of participantHeaders(fakeSsh.logPath)) {
      assert.equal(request.key, "operator");
      assert.equal(request.baseKey, null);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("a unique key that would pass the length limit is refused at start-up, not truncated", () => {
  assert.throws(
    () => resolveParticipantIdentity({ ARMADA_PARTICIPANT_KEY: "k".repeat(125), ARMADA_PARTICIPANT_KEY_UNIQUE: "1" }),
    /ARMADA_PARTICIPANT_KEY must be 1-128 characters/);
});
