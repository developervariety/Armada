import assert from "node:assert/strict";
import { chmod, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PassThrough } from "node:stream";
import test from "node:test";

import {
  McpInputParser,
  createJsonRpcError,
  parseHttpResponse,
  parseMcpResponseMessages,
  runBridge,
} from "../mcp-ssh-http-bridge.mjs";

test("parses newline-delimited JSON-RPC messages", () => {
  const messages = [];
  const parser = new McpInputParser((message) => messages.push(message));
  parser.push(Buffer.from('{"id":1}\n{"id":2}\n'));
  parser.end();

  assert.deepEqual(messages, ['{"id":1}', '{"id":2}']);
});

test("parses Content-Length framed JSON-RPC messages", () => {
  const messages = [];
  const parser = new McpInputParser((message) => messages.push(message));
  const body = '{"jsonrpc":"2.0","id":1,"method":"ping"}';
  parser.push(Buffer.from(`Content-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`));
  parser.end();

  assert.deepEqual(messages, [body]);
});

test("parses Content-Length frames with additional headers", () => {
  const messages = [];
  const parser = new McpInputParser((message) => messages.push(message));
  const body = '{"jsonrpc":"2.0","id":2,"method":"tools/list"}';
  parser.push(Buffer.from(
    `Content-Length: ${Buffer.byteLength(body)}\r\nContent-Type: application/json\r\n\r\n${body}`,
  ));
  parser.end();

  assert.deepEqual(messages, [body]);
});

test("parses HTTP status, session identifier, and body", () => {
  const output = Buffer.from(
    "HTTP/1.1 200 OK\r\nMCP-Session-Id: session-123\r\nContent-Type: application/json\r\n\r\n" +
    '{"jsonrpc":"2.0","id":1,"result":{}}',
  );

  assert.deepEqual(parseHttpResponse(output), {
    statusCode: 200,
    sessionId: "session-123",
    contentType: "application/json",
    body: '{"jsonrpc":"2.0","id":1,"result":{}}',
  });
});

test("parses JSON-RPC messages from a Streamable HTTP SSE response", () => {
  const messages = parseMcpResponseMessages({
    contentType: "text/event-stream; charset=utf-8",
    body:
      'event: message\ndata: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":1}}\n\n' +
      'event: message\ndata: {"jsonrpc":"2.0","id":1,"result":{"resultType":"complete"}}\n\n',
  });

  assert.equal(messages.length, 2);
  assert.equal(JSON.parse(messages[0]).method, "notifications/progress");
  assert.equal(JSON.parse(messages[1]).id, 1);
});

test("creates a JSON-RPC transport error with the original request identifier", () => {
  assert.deepEqual(JSON.parse(createJsonRpcError(42, "unavailable")), {
    jsonrpc: "2.0",
    id: 42,
    error: {
      code: -32603,
      message: "unavailable",
    },
  });
});

test("keeps the bridge alive after one failed request", async () => {
  const input = new PassThrough();
  const output = new PassThrough();
  const errorOutput = new PassThrough();
  const outputChunks = [];
  output.on("data", (chunk) => outputChunks.push(chunk));
  let callCount = 0;

  const bridge = runBridge({
    input,
    output,
    errorOutput,
    request: async (message) => {
      callCount++;
      if (callCount === 1) {
        throw new Error("Admiral is restarting.");
      }

      const request = JSON.parse(message);
      return {
        statusCode: 200,
        sessionId: "session-after-restart",
        contentType: "application/json",
        body: JSON.stringify({
          jsonrpc: "2.0",
          id: request.id,
          result: { recovered: true },
        }),
      };
    },
  });

  input.end(
    '{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n' +
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}\n',
  );
  await bridge;

  const responses = Buffer.concat(outputChunks)
    .toString("utf8")
    .trim()
    .split("\n")
    .map((line) => JSON.parse(line));
  assert.equal(responses.length, 2);
  assert.equal(responses[0].id, 1);
  assert.match(responses[0].error.message, /Admiral is restarting/);
  assert.deepEqual(responses[1], {
    jsonrpc: "2.0",
    id: 2,
    result: { recovered: true },
  });
});

test("forwards notifications without writing a stdio response", async () => {
  const input = new PassThrough();
  const output = new PassThrough();
  const errorOutput = new PassThrough();
  const outputChunks = [];
  output.on("data", (chunk) => outputChunks.push(chunk));

  const bridge = runBridge({
    input,
    output,
    errorOutput,
    request: async () => ({
      statusCode: 200,
      sessionId: "session-1",
      contentType: "application/json",
      body: '{"jsonrpc":"2.0","id":null,"result":{}}',
    }),
  });

  input.end('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}\n');
  await bridge;

  assert.equal(Buffer.concat(outputChunks).length, 0);
});

test("sends a stdio request through the configured SSH command", async () => {
  const directory = await mkdtemp(join(tmpdir(), "armada-mcp-bridge-"));
  try {
    const fakeSsh = join(directory, "fake-ssh");
    const argsLog = join(directory, "args.log");
    await writeFile(fakeSsh, `#!/bin/sh
printf '%s' "$*" > "$FAKE_SSH_ARGS_LOG"
cat >/dev/null
printf 'HTTP/1.1 200 OK\\r\\nMCP-Session-Id: session-live\\r\\nContent-Type: application/json\\r\\n\\r\\n'
printf '%s' '{"jsonrpc":"2.0","id":7,"result":{"live":true}}'
`);
    await chmod(fakeSsh, 0o700);

    const input = new PassThrough();
    const output = new PassThrough();
    const outputChunks = [];
    output.on("data", (chunk) => outputChunks.push(chunk));
    const bridge = runBridge({
      input,
      output,
      errorOutput: new PassThrough(),
      environment: {
        ...process.env,
        ARMADA_SSH_COMMAND: fakeSsh,
        ARMADA_SSH_HOST: "admiral-host",
        ARMADA_SSH_USER: "operator",
        FAKE_SSH_ARGS_LOG: argsLog,
      },
    });

    input.end('{"jsonrpc":"2.0","id":7,"method":"tools/list"}\n');
    await bridge;

    assert.deepEqual(JSON.parse(Buffer.concat(outputChunks).toString("utf8")), {
      jsonrpc: "2.0",
      id: 7,
      result: { live: true },
    });
    const args = await readFile(argsLog, "utf8");
    assert.match(args, /operator@admiral-host/);
    assert.match(args, /http:\/\/localhost:7891\/mcp/);
    assert.match(args, /Accept: application\/json, text\/event-stream/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("mirrors modern MCP request metadata into HTTP headers without a session", async () => {
  const directory = await mkdtemp(join(tmpdir(), "armada-mcp-bridge-modern-"));
  try {
    const fakeSsh = join(directory, "fake-ssh");
    const argsLog = join(directory, "args.log");
    await writeFile(fakeSsh, `#!/bin/sh
printf '%s' "$*" > "$FAKE_SSH_ARGS_LOG"
cat >/dev/null
printf 'HTTP/1.1 200 OK\\r\\nContent-Type: application/json\\r\\n\\r\\n'
printf '%s' '{"jsonrpc":"2.0","id":9,"result":{"resultType":"complete"}}'
`);
    await chmod(fakeSsh, 0o700);

    const input = new PassThrough();
    const output = new PassThrough();
    const bridge = runBridge({
      input,
      output,
      errorOutput: new PassThrough(),
      environment: {
        ...process.env,
        ARMADA_SSH_COMMAND: fakeSsh,
        ARMADA_SSH_HOST: "admiral-host",
        ARMADA_SSH_USER: "operator",
        FAKE_SSH_ARGS_LOG: argsLog,
      },
    });

    input.end(JSON.stringify({
      jsonrpc: "2.0",
      id: 9,
      method: "tools/call",
      params: {
        name: "armada_status",
        arguments: {},
        _meta: {
          "io.modelcontextprotocol/protocolVersion": "2026-07-28",
          "io.modelcontextprotocol/clientCapabilities": {},
        },
      },
    }) + "\n");
    await bridge;

    const args = await readFile(argsLog, "utf8");
    assert.match(args, /MCP-Protocol-Version: 2026-07-28/);
    assert.match(args, /Mcp-Method: tools\/call/);
    assert.match(args, /Mcp-Name: armada_status/);
    assert.doesNotMatch(args, /MCP-Session-Id/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
