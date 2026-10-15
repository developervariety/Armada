namespace Test.Shared.Infrastructure
{
    using System.Collections.Generic;

    /// <summary>
    /// Mux 1.4 <c>--output-format jsonl</c> event lines (structured output contract version 2), shaped exactly as
    /// Mux.Core's AgentEventSerializer writes them, and whole runs built from them.
    /// </summary>
    public static class MuxJsonlFixtures
    {
        #region Public-Members

        /// <summary>run_started for gpt-oss:20b on a local Ollama endpoint, 50 iterations.</summary>
        public const string RunStarted = "{\"contractVersion\":2,\"eventType\":\"run_started\",\"timestampUtc\":\"2026-10-15T10:00:00Z\",\"runId\":\"run_1\",\"sessionId\":\"ses_1\",\"endpointName\":\"local-ollama\",\"adapterType\":\"Ollama\",\"baseUrl\":\"http://localhost:11434\",\"model\":\"gpt-oss:20b\",\"commandName\":\"print\",\"approvalPolicy\":\"yolo\",\"maxIterations\":50,\"toolsEnabled\":true}";

        /// <summary>A heartbeat.</summary>
        public const string Heartbeat = "{\"contractVersion\":2,\"eventType\":\"heartbeat\",\"timestampUtc\":\"2026-10-15T10:00:01Z\",\"stepNumber\":1}";

        /// <summary>A routine context status (no warning).</summary>
        public const string ContextStatus = "{\"contractVersion\":2,\"eventType\":\"context_status\",\"timestampUtc\":\"2026-10-15T10:00:01Z\",\"scope\":\"run\",\"estimatedTokens\":900,\"usableInputLimit\":28000,\"remainingTokens\":27100,\"remainingPercent\":96.7,\"warningThresholdTokens\":22000,\"messageCount\":3,\"trigger\":\"iteration\",\"warningLevel\":\"none\"}";

        /// <summary>A read_file proposal.</summary>
        public const string ReadProposed = "{\"contractVersion\":2,\"eventType\":\"tool_call_proposed\",\"timestampUtc\":\"2026-10-15T10:00:02Z\",\"toolCall\":{\"id\":\"call_1\",\"name\":\"read_file\",\"arguments\":{\"file_path\":\"README.md\"}}}";

        /// <summary>Its approval.</summary>
        public const string ReadApproved = "{\"contractVersion\":2,\"eventType\":\"tool_call_approved\",\"timestampUtc\":\"2026-10-15T10:00:02Z\",\"toolCallId\":\"call_1\"}";

        /// <summary>Its completion.</summary>
        public const string ReadCompleted = "{\"contractVersion\":2,\"eventType\":\"tool_call_completed\",\"timestampUtc\":\"2026-10-15T10:00:02Z\",\"toolCallId\":\"call_1\",\"toolName\":\"read_file\",\"elapsedMs\":3,\"result\":{\"toolCallId\":\"call_1\",\"success\":true,\"content\":\"hello\"}}";

        /// <summary>A proposal of a tool the model invented.</summary>
        public const string InventedProposed = "{\"contractVersion\":2,\"eventType\":\"tool_call_proposed\",\"timestampUtc\":\"2026-10-15T10:00:03Z\",\"toolCall\":{\"id\":\"call_2\",\"name\":\"open_browser\",\"arguments\":{}}}";

        /// <summary>The denial of the invented tool (mux then exits 2).</summary>
        public const string ToolCallDenied = "{\"contractVersion\":2,\"eventType\":\"error\",\"timestampUtc\":\"2026-10-15T10:00:03Z\",\"code\":\"tool_call_denied\",\"errorCode\":\"tool_call_denied\",\"message\":\"Tool 'open_browser' is not available.\",\"failureCategory\":\"approval\",\"model\":\"gpt-oss:20b\"}";

        /// <summary>The iteration limit error.</summary>
        public const string MaxIterationsError = "{\"contractVersion\":2,\"eventType\":\"error\",\"timestampUtc\":\"2026-10-15T10:05:00Z\",\"code\":\"max_iterations_reached\",\"errorCode\":\"max_iterations_reached\",\"message\":\"Agent loop reached the maximum of 50 iterations.\",\"failureCategory\":\"runtime\",\"model\":\"gpt-oss:20b\"}";

        /// <summary>A connection error.</summary>
        public const string ConnectionError = "{\"contractVersion\":2,\"eventType\":\"error\",\"timestampUtc\":\"2026-10-15T10:00:04Z\",\"code\":\"llm_connection_error\",\"errorCode\":\"llm_connection_error\",\"message\":\"Failed to connect to http://localhost:11434: Connection refused\",\"failureCategory\":\"network\"}";

        /// <summary>An authentication failure of the model call.</summary>
        public const string UnauthorizedError = "{\"contractVersion\":2,\"eventType\":\"error\",\"timestampUtc\":\"2026-10-15T10:00:04Z\",\"code\":\"llm_error\",\"errorCode\":\"llm_error\",\"message\":\"LLM request to https://api.example.com failed with status 401: invalid api key\",\"failureCategory\":\"backend\"}";

        /// <summary>A completed run with errors, with usage.</summary>
        public const string CompletedWithErrors = "{\"contractVersion\":2,\"eventType\":\"run_completed\",\"timestampUtc\":\"2026-10-15T10:00:09Z\",\"runId\":\"run_1\",\"sessionId\":\"ses_1\",\"status\":\"completed_with_errors\",\"iterationsCompleted\":3,\"toolCallCount\":2,\"errorCount\":1,\"assistantTextChars\":80,\"durationMs\":4200,\"finalEstimatedTokens\":900,\"compactionCount\":0,\"usage\":{\"inputTokens\":1200,\"outputTokens\":340,\"cachedTokens\":100,\"reasoningTokens\":0,\"totalTokens\":1540,\"estimatedTokens\":900}}";

        /// <summary>A run stopped at the iteration limit.</summary>
        public const string CompletedMaxIterations = "{\"contractVersion\":2,\"eventType\":\"run_completed\",\"timestampUtc\":\"2026-10-15T10:05:00Z\",\"runId\":\"run_1\",\"status\":\"max_iterations_reached\",\"iterationsCompleted\":50,\"toolCallCount\":49,\"errorCount\":1}";

        /// <summary>The first turn's text, split across deltas.</summary>
        public static readonly List<string> FirstTurnText = new List<string>
        {
            Text("I will read "),
            Text("the README.\n")
        };

        /// <summary>The final turn's text, with a status marker split across deltas.</summary>
        public static readonly List<string> FinalTurnText = new List<string>
        {
            Text("All done, the work is committed.\n[ARMA"),
            Text("DA:STATUS] Testing\nFinished")
        };

        /// <summary>The final turn's text, joined.</summary>
        public const string FinalReply = "All done, the work is committed.\n[ARMADA:STATUS] Testing\nFinished";

        #endregion

        #region Public-Methods

        /// <summary>
        /// An assistant_text delta.
        /// </summary>
        /// <param name="text">Delta text (no quotes or backslashes other than newlines).</param>
        /// <returns>The event line.</returns>
        public static string Text(string text)
        {
            return "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"timestampUtc\":\"2026-10-15T10:00:02Z\",\"text\":\"" + text.Replace("\n", "\\n") + "\"}";
        }

        /// <summary>
        /// A run that hits an invented tool, then answers: mux reports completed_with_errors and exits 2.
        /// </summary>
        /// <returns>The lines.</returns>
        public static List<string> CompletedWithErrorsRun()
        {
            List<string> lines = new List<string> { RunStarted, ContextStatus };
            lines.AddRange(FirstTurnText);
            lines.Add(ReadProposed);
            lines.Add(ReadApproved);
            lines.Add(ReadCompleted);
            lines.Add(Heartbeat);
            lines.Add(InventedProposed);
            lines.Add(ToolCallDenied);
            lines.Add(Heartbeat);
            lines.AddRange(FinalTurnText);
            lines.Add(CompletedWithErrors);
            return lines;
        }

        /// <summary>
        /// A run that stops at the iteration limit: mux reports max_iterations_reached and exits 1.
        /// </summary>
        /// <returns>The lines.</returns>
        public static List<string> MaxIterationsRun()
        {
            List<string> lines = new List<string> { RunStarted };
            lines.AddRange(FirstTurnText);
            lines.Add(ReadProposed);
            lines.Add(ReadCompleted);
            lines.Add(Heartbeat);
            lines.Add(MaxIterationsError);
            lines.Add(CompletedMaxIterations);
            return lines;
        }

        #endregion
    }
}
