/**
 * Armada permission gate for pi (Approve in Armada).
 *
 * Armada loads this extension with `pi --extension <path>` when a pi captain runs under the ApproveInArmada CLI
 * permission policy. pi never prompts on its own, so this extension's `tool_call` handler asks Armada instead: it
 * calls the Armada MCP tool `cli_permission_prompt` over the launch's scoped MCP connection and waits for an approver
 * (dashboard Approvals, Ask, or a CLI permission rule) to decide.
 *
 * - Allowed: the tool runs.
 * - Denied: the tool is blocked and the reason becomes the tool's error result, so the model sees it and continues.
 * - Armada unreachable, a protocol error, a missing URL or token, or no decision before the timeout: the tool is
 *   blocked (fail closed). A handler that throws also blocks the tool in pi.
 *
 * Tools that only read, search or edit files in the working directory (read, edit, write, grep, find, ls), the
 * tool_search/codemode orchestrators (their nested tool calls come back through this handler) and Armada's own MCP
 * tools (governed by Armada's per-tool authorization) run without a prompt, like Claude Code's acceptEdits mode.
 * Every other tool, including bash, powershell and any tool a repository extension adds, needs approval.
 *
 * Environment (set by Armada for the launch):
 *   ARMADA_MCP_URL                     Armada MCP endpoint (http://host:port/mcp)
 *   ARMADA_MCP_TOKEN                   mission- or thread-scoped session token (sent as X-Token)
 *   ARMADA_PERMISSION_TIMEOUT_SECONDS  seconds to wait for a decision (Armada's own prompt timeout plus a margin)
 *
 * Written as plain JavaScript (types in JSDoc) so pi's TypeScript loader and a plain `node` test harness both run it.
 */

const NO_PROMPT_TOOLS = new Set(["read", "edit", "write", "grep", "find", "ls", "tool_search", "codemode"]);
const DEFAULT_TIMEOUT_SECONDS = 720;
const PROMPT_TOOL = "cli_permission_prompt";

/**
 * Whether a pi tool call must be approved in Armada.
 * @param {string} toolName pi tool name
 * @returns {boolean}
 */
export function needsApproval(toolName) {
	if (typeof toolName !== "string" || toolName.length === 0) return true;
	if (NO_PROMPT_TOOLS.has(toolName)) return false;
	if (toolName.startsWith("mcp__armada__")) return false;
	return true;
}

/**
 * The tool name Armada sees: pi's bash tool is reported as Claude Code's "Bash" so Bash permission rules
 * (for example Bash(git status:*)) apply to it; every other name is passed through.
 * @param {string} toolName pi tool name
 * @returns {string}
 */
export function armadaToolName(toolName) {
	return toolName === "bash" ? "Bash" : toolName;
}

/**
 * Read the JSON-RPC response for a request id from a JSON or text/event-stream body.
 * @param {string} body response body
 * @param {number} id request id
 * @returns {any}
 */
function readRpcResponse(body, id) {
	const text = (body || "").trim();
	if (text.startsWith("{")) return JSON.parse(text);
	let found = null;
	for (const line of text.split(/\r?\n/)) {
		if (!line.startsWith("data:")) continue;
		const payload = line.substring(5).trim();
		if (!payload.startsWith("{")) continue;
		const message = JSON.parse(payload);
		if (message && message.id === id) found = message;
	}
	if (found === null) throw new Error("no JSON-RPC response in the MCP reply");
	return found;
}

/**
 * Create the gate. Exported for tests; pi uses the default export.
 * @param {{ url?: string, token?: string, timeoutSeconds?: number, fetch?: typeof fetch }} options
 */
export function createGate(options) {
	const url = options.url || "";
	const token = options.token || "";
	const timeoutSeconds = options.timeoutSeconds && options.timeoutSeconds > 0 ? options.timeoutSeconds : DEFAULT_TIMEOUT_SECONDS;
	const doFetch = options.fetch || globalThis.fetch;
	let nextId = 1;
	let queue = Promise.resolve();

	/**
	 * POST one JSON-RPC message.
	 * @param {any} message
	 * @param {string | null} sessionId
	 * @param {AbortSignal} signal
	 */
	async function post(message, sessionId, signal) {
		/** @type {Record<string, string>} */
		const headers = { "Content-Type": "application/json", Accept: "application/json, text/event-stream", "X-Token": token };
		if (sessionId) headers["Mcp-Session-Id"] = sessionId;
		const response = await doFetch(url, { method: "POST", headers, body: JSON.stringify(message), signal });
		const body = await response.text();
		if (!response.ok) throw new Error("Armada MCP returned HTTP " + response.status);
		return { response, body };
	}

	/**
	 * Ask Armada about one tool call.
	 * @param {string} toolName
	 * @param {any} input
	 * @param {string | undefined} toolCallId
	 * @returns {Promise<{ allowed: boolean, reason: string }>}
	 */
	async function ask(toolName, input, toolCallId) {
		if (!url || !token) {
			return { allowed: false, reason: "Approve in Armada: the launch has no Armada MCP URL or token, so " + toolName + " was blocked." };
		}

		const controller = new AbortController();
		const timer = setTimeout(() => controller.abort(), timeoutSeconds * 1000);
		try {
			const initId = nextId++;
			const init = await post(
				{ jsonrpc: "2.0", id: initId, method: "initialize", params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "armada-pi-permission-gate", version: "1.0" } } },
				null,
				controller.signal,
			);
			const sessionId = init.response.headers.get("mcp-session-id");
			const callId = nextId++;
			const call = await post(
				{ jsonrpc: "2.0", id: callId, method: "tools/call", params: { name: PROMPT_TOOL, arguments: { tool_name: armadaToolName(toolName), input: input ?? {}, tool_use_id: toolCallId ?? "" } } },
				sessionId,
				controller.signal,
			);
			const rpc = readRpcResponse(call.body, callId);
			if (rpc.error) throw new Error("Armada MCP error: " + (rpc.error.message || JSON.stringify(rpc.error)));
			const content = rpc.result && Array.isArray(rpc.result.content) ? rpc.result.content : [];
			const text = content.length > 0 && typeof content[0].text === "string" ? content[0].text : "";
			/** @type {any} */
			let decision = null;
			try {
				decision = JSON.parse(text);
			} catch {
				decision = null;
			}
			if (!rpc.result || rpc.result.isError === true || !decision || typeof decision.behavior !== "string") {
				const detail = decision && decision.message ? decision.message : text || "no decision";
				return { allowed: false, reason: "Approve in Armada: " + toolName + " was blocked because Armada did not return a decision (" + detail + ")." };
			}
			if (decision.behavior === "allow") return { allowed: true, reason: "" };
			const message = typeof decision.message === "string" && decision.message.length > 0 ? decision.message : "Denied in Armada.";
			return { allowed: false, reason: message };
		} catch (error) {
			const why = controller.signal.aborted
				? "no decision arrived within " + timeoutSeconds + " seconds"
				: "Armada could not be reached (" + (error instanceof Error ? error.message : String(error)) + ")";
			return { allowed: false, reason: "Approve in Armada: " + toolName + " was blocked because " + why + "." };
		} finally {
			clearTimeout(timer);
		}
	}

	/**
	 * The pi tool_call handler: undefined lets the tool run, { block, reason } blocks it. Calls are decided one at a
	 * time in arrival order.
	 * @param {{ toolName: string, input?: any, toolCallId?: string }} event
	 */
	function onToolCall(event) {
		if (!needsApproval(event.toolName)) return Promise.resolve(undefined);
		const decided = queue.then(() => ask(event.toolName, event.input, event.toolCallId));
		queue = decided.then(
			() => undefined,
			() => undefined,
		);
		return decided.then(
			(decision) => (decision.allowed ? undefined : { block: true, reason: decision.reason }),
			(error) => ({ block: true, reason: "Approve in Armada: " + event.toolName + " was blocked (" + String(error) + ")." }),
		);
	}

	return { onToolCall, ask };
}

/**
 * pi extension entry point.
 * @param {{ on: (event: string, handler: (event: any, ctx: any) => any) => void }} pi
 */
export default function (pi) {
	const timeout = Number(process.env.ARMADA_PERMISSION_TIMEOUT_SECONDS || "");
	const gate = createGate({
		url: process.env.ARMADA_MCP_URL,
		token: process.env.ARMADA_MCP_TOKEN,
		timeoutSeconds: Number.isFinite(timeout) && timeout > 0 ? timeout : DEFAULT_TIMEOUT_SECONDS,
	});
	pi.on("tool_call", async (event) => gate.onToolCall(event));
}
