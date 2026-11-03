// Behaviour tests for the MCP SSH bridge. Run with: node --test scripts/mcp-ssh-http-bridge.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { PassThrough } from "node:stream";
import { runBridge } from "./mcp-ssh-http-bridge.mjs";

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

test("requests carry the transport headers and no session-identity header, whatever the environment holds", async () => {
  const directory = mkdtempSync(join(tmpdir(), "bridge-headers-"));
  try {
    const fakeSsh = createFakeSsh(directory);

    await runBridgeProcess(fakeSsh, { ARMADA_PARTICIPANT_KEY: "operator", ARMADA_PARTICIPANT_KEY_UNIQUE: "1" });

    const sent = readFileSync(fakeSsh.logPath, "utf8").split("\n").filter((line) => line.length > 0);
    assert.equal(sent.length, 2, "the process sent two requests");
    for (const command of sent) {
      assert.match(command, /Content-Type: application\/json/);
      assert.doesNotMatch(command, /participant/i);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

// Drive runBridge in process with a stand-in request function, so a test controls when each
// remote call answers.
function startInProcessBridge(request) {
  const input = new PassThrough();
  const output = new PassThrough();
  const lines = [];
  let buffered = "";
  const waiters = [];
  output.on("data", (chunk) => {
    buffered += chunk.toString("utf8");
    let index;
    while ((index = buffered.indexOf("\n")) >= 0) {
      const line = buffered.substring(0, index).trim();
      buffered = buffered.substring(index + 1);
      if (line.length > 0) lines.push(JSON.parse(line));
    }
    for (const waiter of waiters.splice(0)) waiter();
  });
  const done = runBridge({ input, output, errorOutput: new PassThrough(), environment: {}, request });
  const send = (id, name) => input.write(JSON.stringify({ jsonrpc: "2.0", id, method: "tools/call", params: { name, arguments: {} } }) + "\n");
  const waitFor = (predicate, milliseconds, label) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(label)), milliseconds);
    const check = () => { if (predicate()) { clearTimeout(timer); resolve(); } else waiters.push(check); };
    check();
  });
  return { input, lines, done, send, waitFor };
}

function reply(id, sessionId) {
  return { statusCode: 200, sessionId, contentType: "application/json", body: JSON.stringify({ jsonrpc: "2.0", id, result: {} }) };
}

test("a read is answered while an earlier long write is still waiting for its response", async () => {
  let releaseWrite;
  const writeHeld = new Promise((resolve) => { releaseWrite = resolve; });
  const bridge = startInProcessBridge(async (message, sessionId, requestObject) => {
    if (requestObject.id === 1) return reply(1, null);
    if (requestObject.id === 2) { await writeHeld; return reply(2, null); }
    return reply(requestObject.id, null);
  });

  bridge.send(1, "armada_status");
  await bridge.waitFor(() => bridge.lines.some((line) => line.id === 1), 2000, "the first call did not answer");
  bridge.send(2, "armada_review_hold");
  bridge.send(3, "armada_status");
  await bridge.waitFor(() => bridge.lines.some((line) => line.id === 3), 2000,
    "the read waited behind the held write");
  assert.equal(bridge.lines.some((line) => line.id === 2), false, "the write is still held when the read answers");

  releaseWrite();
  await bridge.waitFor(() => bridge.lines.some((line) => line.id === 2), 2000, "the held write did not answer after release");
  bridge.input.end();
  await bridge.done;
  assert.deepEqual(bridge.lines.map((line) => line.id).sort(), [1, 2, 3], "each call is answered exactly once");
});

test("calls sent while the first call is in flight carry the session id that call returns", async () => {
  let releaseFirst;
  const firstHeld = new Promise((resolve) => { releaseFirst = resolve; });
  const seen = [];
  const bridge = startInProcessBridge(async (message, sessionId, requestObject) => {
    seen.push({ id: requestObject.id, sessionId });
    if (requestObject.id === 1) { await firstHeld; return reply(1, "session-one"); }
    return reply(requestObject.id, null);
  });

  bridge.send(1, "armada_status");
  bridge.send(2, "armada_status");
  bridge.send(3, "armada_status");
  await new Promise((resolve) => setTimeout(resolve, 50));
  assert.deepEqual(seen.map((call) => call.id), [1], "later calls wait until the first call has answered");

  releaseFirst();
  await bridge.waitFor(() => bridge.lines.length === 3, 2000, "not every call answered");
  bridge.input.end();
  await bridge.done;
  assert.deepEqual(seen.filter((call) => call.id !== 1).map((call) => call.sessionId), ["session-one", "session-one"],
    "the later calls carry the session id from the first response");
});
