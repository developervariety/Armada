// Behaviour tests for the MCP SSH bridge. Run with: node --test scripts/mcp-ssh-http-bridge.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

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
