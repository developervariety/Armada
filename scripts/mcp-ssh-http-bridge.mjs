#!/usr/bin/env node

import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { pathToFileURL } from "node:url";

const DEFAULT_MCP_URL = "http://localhost:7891/mcp";
const DEFAULT_REQUEST_TIMEOUT_SECONDS = 600;
const MAX_HEADER_BYTES = 8192;
const MAX_BODY_BYTES = 16 * 1024 * 1024;

export class McpInputParser {
  constructor(onMessage) {
    if (typeof onMessage !== "function") {
      throw new TypeError("onMessage must be a function.");
    }

    this._Buffer = Buffer.alloc(0);
    this._ExpectedBodyBytes = null;
    this._OnMessage = onMessage;
  }

  push(chunk) {
    if (!Buffer.isBuffer(chunk)) {
      chunk = Buffer.from(chunk);
    }

    this._Buffer = Buffer.concat([this._Buffer, chunk]);
    this._Drain();
  }

  end() {
    if (this._ExpectedBodyBytes !== null) {
      throw new Error("Unexpected end of input while reading an MCP frame body.");
    }

    const remainder = this._Buffer.toString("utf8").trim();
    this._Buffer = Buffer.alloc(0);
    if (remainder.length > 0) {
      this._OnMessage(remainder);
    }
  }

  _Drain() {
    while (this._Buffer.length > 0) {
      if (this._ExpectedBodyBytes !== null) {
        if (this._Buffer.length < this._ExpectedBodyBytes) {
          return;
        }

        const body = this._Buffer.subarray(0, this._ExpectedBodyBytes);
        this._Buffer = this._Buffer.subarray(this._ExpectedBodyBytes);
        this._ExpectedBodyBytes = null;
        this._OnMessage(body.toString("utf8"));
        continue;
      }

      const lineEnd = this._Buffer.indexOf(10);
      if (lineEnd < 0) {
        if (this._Buffer.length > MAX_HEADER_BYTES) {
          throw new Error("MCP message line is too large.");
        }
        return;
      }

      const rawLine = this._Buffer.subarray(0, lineEnd + 1);
      this._Buffer = this._Buffer.subarray(lineEnd + 1);
      const line = rawLine.toString("utf8").replace(/\r?\n$/, "");
      if (line.length === 0) {
        continue;
      }

      if (!line.toLowerCase().startsWith("content-length:")) {
        this._OnMessage(line);
        continue;
      }

      const lengthText = line.substring(line.indexOf(":") + 1).trim();
      const contentLength = Number.parseInt(lengthText, 10);
      if (!Number.isInteger(contentLength) || contentLength < 0 || contentLength > MAX_BODY_BYTES) {
        throw new Error("MCP frame Content-Length is invalid.");
      }

      const headerBytes = findFrameHeaderBytes(this._Buffer);
      if (headerBytes === null) {
        if (this._Buffer.length > MAX_HEADER_BYTES) {
          throw new Error("MCP frame headers are too large.");
        }
        this._Buffer = Buffer.concat([rawLine, this._Buffer]);
        return;
      }

      this._Buffer = this._Buffer.subarray(headerBytes);
      this._ExpectedBodyBytes = contentLength;
    }
  }
}

export function parseHttpResponse(output) {
  const separator = findHttpHeaderSeparator(output);
  if (separator.index < 0) {
    throw new Error("Armada MCP endpoint returned an invalid HTTP response.");
  }

  const headerText = output.subarray(0, separator.index).toString("utf8");
  const body = output.subarray(separator.index + separator.length).toString("utf8");
  const headerLines = headerText.split(/\r?\n/);
  const statusMatch = /^HTTP\/\d(?:\.\d)?\s+(\d{3})\b/.exec(headerLines[0] || "");
  if (!statusMatch) {
    throw new Error("Armada MCP endpoint returned an invalid HTTP status line.");
  }

  let sessionId = null;
  let contentType = null;
  for (const headerLine of headerLines.slice(1)) {
    const colonIndex = headerLine.indexOf(":");
    if (colonIndex <= 0) {
      continue;
    }

    const name = headerLine.substring(0, colonIndex).trim().toLowerCase();
    if (name === "mcp-session-id") {
      sessionId = headerLine.substring(colonIndex + 1).trim();
    } else if (name === "content-type") {
      contentType = headerLine.substring(colonIndex + 1).trim();
    }
  }

  return {
    statusCode: Number.parseInt(statusMatch[1], 10),
    sessionId,
    contentType,
    body,
  };
}

export function parseMcpResponseMessages(response) {
  const body = response.body.trim();
  if (body.length === 0) {
    return [];
  }

  const contentType = (response.contentType || "").toLowerCase();
  if (!contentType.startsWith("text/event-stream") && !looksLikeSse(body)) {
    JSON.parse(body);
    return [body];
  }

  const messages = [];
  const events = body.split(/\r?\n\r?\n/);
  for (const event of events) {
    const dataLines = [];
    for (const line of event.split(/\r?\n/)) {
      if (line.startsWith(":")) {
        continue;
      }
      if (line === "data") {
        dataLines.push("");
      } else if (line.startsWith("data:")) {
        dataLines.push(line.substring(5).replace(/^ /, ""));
      }
    }

    if (dataLines.length === 0) {
      continue;
    }

    const data = dataLines.join("\n").trim();
    if (data.length === 0 || data === "[DONE]") {
      continue;
    }

    JSON.parse(data);
    messages.push(data);
  }

  return messages;
}

export function createJsonRpcError(id, message) {
  return JSON.stringify({
    jsonrpc: "2.0",
    id: id ?? null,
    error: {
      code: -32603,
      message,
    },
  });
}

export async function runBridge(options = {}) {
  const input = options.input || process.stdin;
  const output = options.output || process.stdout;
  const errorOutput = options.errorOutput || process.stderr;
  const environment = options.environment || process.env;
  const request = options.request ||
    ((message, sessionId, requestObject) => sendRequestOverSsh(message, sessionId, requestObject, environment));
  let sessionId = null;
  let queue = Promise.resolve();

  const parser = new McpInputParser((message) => {
    queue = queue.then(async () => {
      let requestObject;
      try {
        requestObject = JSON.parse(message);
      } catch {
        output.write(createJsonRpcError(null, "Invalid JSON-RPC request.") + "\n");
        return;
      }

      const hasId = Object.prototype.hasOwnProperty.call(requestObject, "id");
      try {
        const response = await request(message, sessionId, requestObject);
        if (response.sessionId) {
          sessionId = validateSessionId(response.sessionId);
        }

        if (!hasId) {
          return;
        }

        if (response.statusCode < 200 || response.statusCode >= 300) {
          const detail = response.body.trim();
          output.write(createJsonRpcError(
            requestObject.id,
            detail || `Armada MCP endpoint returned HTTP ${response.statusCode}.`,
          ) + "\n");
          return;
        }

        const messages = parseMcpResponseMessages(response);
        if (messages.length === 0) {
          output.write(createJsonRpcError(requestObject.id, "Armada MCP endpoint returned an empty response.") + "\n");
          return;
        }

        for (const responseMessage of messages) {
          output.write(responseMessage + "\n");
        }
      } catch (error) {
        const messageText = error instanceof Error ? error.message : String(error);
        if (hasId) {
          output.write(createJsonRpcError(
            requestObject.id,
            "Armada MCP bridge request failed: " + messageText,
          ) + "\n");
        } else {
          errorOutput.write("Armada MCP bridge notification failed: " + messageText + "\n");
        }
      }
    });
  });

  input.on("data", (chunk) => {
    try {
      parser.push(chunk);
    } catch (error) {
      const messageText = error instanceof Error ? error.message : String(error);
      output.write(createJsonRpcError(null, messageText) + "\n");
    }
  });

  await new Promise((resolve, reject) => {
    input.on("end", resolve);
    input.on("error", reject);
  });

  parser.end();
  await queue;
}

export function buildSshArgs(environment, remoteCommand) {
  const host = environment.ARMADA_SSH_HOST || "armada";
  const user = environment.ARMADA_SSH_USER;
  const target = user ? `${user}@${host}` : host;
  const args = [
    "-o",
    "BatchMode=yes",
    "-o",
    "ConnectTimeout=10",
  ];
  const identity = environment.ARMADA_SSH_KEY;
  if (identity && existsSync(identity)) {
    args.push("-o", "IdentitiesOnly=yes", "-i", identity);
  } else if (identity && existsSync(identity + ".pub")) {
    args.push("-o", "IdentitiesOnly=yes", "-i", identity + ".pub");
  }

  args.push(target, remoteCommand);
  return args;
}

async function sendRequestOverSsh(message, sessionId, requestObject, environment) {
  const sshCommand = environment.ARMADA_SSH_COMMAND || "ssh";
  const mcpUrl = environment.ARMADA_MCP_URL || DEFAULT_MCP_URL;
  const timeoutSeconds = parseTimeoutSeconds(environment.ARMADA_MCP_TIMEOUT_SEC);
  const headers = [
    "--header",
    shellQuote("Content-Type: application/json"),
    "--header",
    shellQuote("Accept: application/json, text/event-stream"),
  ];
  const protocolVersion = requestObject?.params?._meta?.["io.modelcontextprotocol/protocolVersion"];
  if (typeof protocolVersion === "string" && protocolVersion.length > 0) {
    headers.push("--header", shellQuote("MCP-Protocol-Version: " + encodeHeaderValue(protocolVersion)));
    headers.push("--header", shellQuote("Mcp-Method: " + encodeHeaderValue(requestObject.method)));

    const requestName = getRequestName(requestObject);
    if (requestName !== null) {
      headers.push("--header", shellQuote("Mcp-Name: " + encodeHeaderValue(requestName)));
    }
  } else if (sessionId) {
    headers.push("--header", shellQuote("MCP-Session-Id: " + validateSessionId(sessionId)));
  }

  const remoteCommand = [
    "curl",
    "--silent",
    "--show-error",
    "--dump-header",
    "-",
    "--output",
    "-",
    "--connect-timeout",
    "10",
    "--max-time",
    String(timeoutSeconds),
    ...headers,
    "--data-binary",
    "@-",
    shellQuote(mcpUrl),
  ].join(" ");

  const child = spawn(sshCommand, buildSshArgs(environment, remoteCommand), {
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
    env: environment,
  });
  const stdoutChunks = [];
  const stderrChunks = [];
  child.stdout.on("data", (chunk) => stdoutChunks.push(chunk));
  child.stderr.on("data", (chunk) => stderrChunks.push(chunk));
  child.stdin.end(message);

  const exitCode = await new Promise((resolve, reject) => {
    child.on("error", reject);
    child.on("close", resolve);
  });
  const stdout = Buffer.concat(stdoutChunks);
  const stderr = Buffer.concat(stderrChunks).toString("utf8").trim();
  if (exitCode !== 0) {
    throw new Error(stderr || `SSH request exited with code ${exitCode}.`);
  }

  return parseHttpResponse(stdout);
}

function findFrameHeaderBytes(buffer) {
  if (buffer.length >= 2 && buffer[0] === 13 && buffer[1] === 10) {
    return 2;
  }
  if (buffer.length >= 1 && buffer[0] === 10) {
    return 1;
  }

  const separator = findHttpHeaderSeparator(buffer);
  return separator.index >= 0 ? separator.index + separator.length : null;
}

function findHttpHeaderSeparator(buffer) {
  const windowsIndex = buffer.indexOf("\r\n\r\n");
  if (windowsIndex >= 0) {
    return { index: windowsIndex, length: 4 };
  }

  const unixIndex = buffer.indexOf("\n\n");
  return { index: unixIndex, length: unixIndex >= 0 ? 2 : 0 };
}

function parseTimeoutSeconds(value) {
  if (!value) {
    return DEFAULT_REQUEST_TIMEOUT_SECONDS;
  }

  const parsed = Number.parseInt(value, 10);
  if (!Number.isInteger(parsed) || parsed < 1 || parsed > 3600) {
    throw new Error("ARMADA_MCP_TIMEOUT_SEC must be an integer from 1 through 3600.");
  }
  return parsed;
}

function shellQuote(value) {
  return "'" + String(value).replace(/'/g, "'\"'\"'") + "'";
}

function validateSessionId(value) {
  if (!/^[A-Za-z0-9._:-]{1,256}$/.test(value)) {
    throw new Error("Armada MCP endpoint returned an invalid session identifier.");
  }
  return value;
}

function getRequestName(requestObject) {
  if (!requestObject || !requestObject.params) {
    return null;
  }

  if (requestObject.method === "tools/call" || requestObject.method === "prompts/get") {
    return typeof requestObject.params.name === "string" ? requestObject.params.name : null;
  }
  if (requestObject.method === "resources/read") {
    return typeof requestObject.params.uri === "string" ? requestObject.params.uri : null;
  }

  return null;
}

function encodeHeaderValue(value) {
  const text = String(value);
  const isPlainAscii = text.length > 0
    && text.trim() === text
    && !text.startsWith("=?base64?")
    && !text.endsWith("?=")
    && [...text].every((character) => {
      const code = character.charCodeAt(0);
      return code === 9 || (code >= 32 && code <= 126);
    });
  if (isPlainAscii) {
    return text;
  }

  return "=?base64?" + Buffer.from(text, "utf8").toString("base64") + "?=";
}

function looksLikeSse(body) {
  return /^(?:event|data|id|retry):/m.test(body) || /^:/m.test(body);
}

const isMainModule = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMainModule) {
  await runBridge();
}
