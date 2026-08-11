namespace Armada.Server
{
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using Armada.Server.WebSocket;
    using SyslogLogging;

    /// <summary>
    /// Runs an interactive chat turn with a captain by launching its agent runtime headlessly, the same
    /// way missions and planning sessions drive the CLI. Every runtime (Mux, Claude Code, Codex, Gemini,
    /// Cursor) is invoked through its <see cref="IAgentRuntime"/> adapter in a throwaway working directory;
    /// the agent's final response is read from the runtime's final-message artifact. There is no separate
    /// model-endpoint (PolyPrompt) path: Mux, like the others, is a CLI that runs headless.
    /// </summary>
    public class CaptainChatService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly AgentRuntimeFactory _RuntimeFactory;
        private readonly ArmadaWebSocketHub? _WebSocketHub;
        private readonly LoggingModule _Logging;
        private readonly string _Header = "[CaptainChatService] ";

        // A chat turn spawns a real agent process; bound how long we wait and how much stdout we retain.
        private const int _DefaultTimeoutMs = 300000;
        private const int _MaxOutputChars = 200000;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="runtimeFactory">Agent runtime factory used to launch the captain's CLI headlessly.</param>
        /// <param name="webSocketHub">WebSocket hub used to stream reply chunks live; may be null.</param>
        /// <param name="logging">Logging module.</param>
        public CaptainChatService(DatabaseDriver database, AgentRuntimeFactory runtimeFactory, ArmadaWebSocketHub? webSocketHub, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _RuntimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
            _WebSocketHub = webSocketHub;
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Send one chat turn to a captain and return the reply plus per-turn metrics. The captain's runtime
        /// is launched headlessly in a temporary working directory; the reply is the runtime's final-message
        /// artifact (falling back to accumulated stdout).
        /// </summary>
        /// <param name="captainId">Captain identifier (cpt_ prefix).</param>
        /// <param name="request">The new message and prior conversation.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The assistant reply and its timing statistics.</returns>
        public async Task<CaptainChatResponse> ChatAsync(string captainId, CaptainChatRequest request, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.Message)) return Fail("A message is required.");

            Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (captain == null) return Fail("Captain not found.");

            string prompt = BuildPrompt(captain, request);
            string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-chat-" + Guid.NewGuid().ToString("N"));
            string finalMessageFilePath = Path.Combine(workingDirectory, "reply.txt");

            IAgentRuntime? runtime = null;
            int processId = -1;

            try
            {
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    runtime = _RuntimeFactory.Create(captain.Runtime);
                }
                catch (Exception e)
                {
                    return Fail("This captain's runtime (" + captain.Runtime + ") could not be launched for chat: " + e.Message);
                }

                TaskCompletionSource<int?> exitSource = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                object outputLock = new object();
                StringBuilder output = new StringBuilder();
                DateTime startUtc = DateTime.UtcNow;
                DateTime? firstOutputUtc = null;

                // Per-turn telemetry harvested from the captain's own output. Mux emits JSONL protocol
                // events (run_started/assistant_text/run_completed) carrying model, duration, and token
                // estimates; CLI runtimes that only stream text still yield wall-clock timing.
                bool isMux = captain.Runtime == AgentRuntimeEnum.Mux;
                double? reportedDurationMs = null;
                int? reportedTokens = null;
                string? reportedModel = null;
                string? turnId = request.TurnId;

                runtime.OnOutputReceived += (pid, line) =>
                {
                    if (isMux && MuxRuntime.IsProtocolEventLine(line))
                    {
                        // Parse the Mux event for telemetry and live text. The final reply still comes
                        // from the final-message artifact; assistant_text carries the streamed deltas.
                        try
                        {
                            using (JsonDocument doc = JsonDocument.Parse(line.Trim()))
                            {
                                JsonElement root = doc.RootElement;
                                string eventType = root.TryGetProperty("eventType", out JsonElement et) && et.ValueKind == JsonValueKind.String
                                    ? et.GetString() ?? String.Empty : String.Empty;

                                string? deltaText = null;
                                lock (outputLock)
                                {
                                    if (root.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                                        reportedModel = m.GetString();
                                    if (eventType == "assistant_text")
                                    {
                                        if (firstOutputUtc == null) firstOutputUtc = DateTime.UtcNow;
                                        if (root.TryGetProperty("text", out JsonElement tx) && tx.ValueKind == JsonValueKind.String)
                                            deltaText = tx.GetString();
                                    }
                                    else if (eventType == "run_completed")
                                    {
                                        if (root.TryGetProperty("durationMs", out JsonElement d) && d.ValueKind == JsonValueKind.Number)
                                            reportedDurationMs = d.GetDouble();
                                        if (root.TryGetProperty("finalEstimatedTokens", out JsonElement ft) && ft.ValueKind == JsonValueKind.Number)
                                            reportedTokens = ft.GetInt32();
                                    }
                                }
                                if (!String.IsNullOrEmpty(deltaText)) EmitChunk(turnId, deltaText!);
                            }
                        }
                        catch (JsonException) { }
                        return;
                    }

                    lock (outputLock)
                    {
                        if (firstOutputUtc == null) firstOutputUtc = DateTime.UtcNow;
                        if (output.Length < _MaxOutputChars)
                        {
                            output.Append(line);
                            output.Append('\n');
                        }
                    }
                    EmitChunk(turnId, line + "\n");
                };
                runtime.OnProcessExited += (pid, code) => exitSource.TrySetResult(code);

                processId = await runtime.StartAsync(
                    workingDirectory,
                    prompt,
                    finalMessageFilePath: finalMessageFilePath,
                    model: captain.Model,
                    captain: captain,
                    token: token).ConfigureAwait(false);

                using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeoutCts.CancelAfter(_DefaultTimeoutMs);

                    Task finished = await Task.WhenAny(
                        exitSource.Task,
                        Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);

                    if (finished != exitSource.Task)
                    {
                        try { await runtime.StopAsync(processId, CancellationToken.None).ConfigureAwait(false); }
                        catch { }

                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                        return Fail("The captain did not respond within the time limit.");
                    }
                }

                int? exitCode = await exitSource.Task.ConfigureAwait(false);

                string reply = String.Empty;
                try
                {
                    if (File.Exists(finalMessageFilePath))
                    {
                        string artifact = await File.ReadAllTextAsync(finalMessageFilePath).ConfigureAwait(false);
                        if (!String.IsNullOrWhiteSpace(artifact)) reply = artifact.Trim();
                    }
                }
                catch { }

                if (String.IsNullOrWhiteSpace(reply))
                {
                    lock (outputLock) reply = output.ToString().Trim();
                }

                if (String.IsNullOrWhiteSpace(reply))
                {
                    return Fail(exitCode.HasValue && exitCode.Value != 0
                        ? "The captain exited with code " + exitCode.Value + " before producing a response."
                        : "The captain produced no response.");
                }

                // Keep all timing on one wall-clock base so time-to-first-token never exceeds total and
                // streaming always resolves. Token counts come from the captain's own telemetry (Mux
                // reports finalEstimatedTokens); reportedDurationMs is captain-internal and, being on a
                // different base than our wall clock, is used only as a fallback total.
                double wallClockMs = (DateTime.UtcNow - startUtc).TotalMilliseconds;
                double totalMs = wallClockMs > 0 ? wallClockMs : (reportedDurationMs ?? wallClockMs);
                double? ttftMs = firstOutputUtc.HasValue
                    ? Math.Min((firstOutputUtc.Value - startUtc).TotalMilliseconds, totalMs)
                    : (double?)null;
                double? tokensPerSecond = (reportedTokens.HasValue && totalMs > 0)
                    ? reportedTokens.Value / (totalMs / 1000.0)
                    : (double?)null;

                CaptainChatResponse response = new CaptainChatResponse
                {
                    Success = true,
                    Reply = reply,
                    Model = !String.IsNullOrEmpty(reportedModel) ? reportedModel
                        : (String.IsNullOrEmpty(captain.Model) ? captain.Runtime.ToString() : captain.Model),
                    Metrics = new CaptainChatMetrics
                    {
                        TotalMs = totalMs,
                        TimeToFirstTokenMs = ttftMs,
                        StreamingMs = ttftMs.HasValue ? Math.Max(0, totalMs - ttftMs.Value) : (double?)null,
                        TotalTokens = reportedTokens,
                        TokensPerSecond = tokensPerSecond,
                    },
                };

                _Logging.Debug(_Header + "chat turn for captain " + captainId + " (" + captain.Runtime + "): " +
                    (response.Metrics.TotalMs?.ToString("F0") ?? "?") + "ms, exit " + (exitCode?.ToString() ?? "?"));

                return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _Logging.Warn(_Header + "chat turn failed for captain " + captainId + ": " + e.Message);
                return Fail(e.Message);
            }
            finally
            {
                try { if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, true); }
                catch { }
            }
        }

        #endregion

        #region Private-Methods

        private static string BuildPrompt(Captain captain, CaptainChatRequest request)
        {
            StringBuilder builder = new StringBuilder();

            if (!String.IsNullOrWhiteSpace(captain.SystemInstructions))
            {
                builder.AppendLine(captain.SystemInstructions.Trim());
                builder.AppendLine();
            }

            if (request.History != null && request.History.Count > 0)
            {
                foreach (CaptainChatMessage message in request.History)
                {
                    if (String.IsNullOrWhiteSpace(message.Content)) continue;
                    string speaker = String.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Assistant" : "User";
                    builder.AppendLine(speaker + ": " + message.Content.Trim());
                }
                builder.AppendLine();
            }

            builder.Append("User: " + request.Message.Trim());
            return builder.ToString();
        }

        private void EmitChunk(string? turnId, string delta)
        {
            if (String.IsNullOrEmpty(turnId) || _WebSocketHub == null || String.IsNullOrEmpty(delta)) return;
            try { _WebSocketHub.BroadcastEvent("ask.chunk", String.Empty, new { turnId, delta }); }
            catch { }
        }

        private static CaptainChatResponse Fail(string error)
        {
            return new CaptainChatResponse { Success = false, Error = error };
        }

        #endregion
    }
}
