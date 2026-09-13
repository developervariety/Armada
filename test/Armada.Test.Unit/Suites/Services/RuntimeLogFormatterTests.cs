namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Readable log contracts for supported event shapes and unsafe input.</summary>
    public sealed class RuntimeLogFormatterTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Runtime Log Formatter";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Existing redaction families and ordinary text stay compatible", () =>
            {
                foreach (string secret in new[]
                {
                    "Bearer " + new string('a', 24), "sk-" + new string('a', 24),
                    "ghp_" + new string('b', 24), "AKIA" + new string('C', 16),
                    "api_key=" + new string('d', 24)
                })
                    AssertFalse(RuntimeLogFormatter.RedactSecrets(secret).Contains(secret));
                string ordinary = "{\"text\":\"line\\nnext\",\"count\":5}";
                AssertEqual(ordinary, RuntimeLogFormatter.RedactSecrets(ordinary));
            });

            await RunTest("Escaped JSON credentials and provider keys are fully redacted", () =>
            {
                string suffix = "still-secret";
                string escaped = "{\"password\":\"abcdef\\\"" + suffix + "\"}";
                string encodedKey = "{\"pass\\u0077ord\":\"" + suffix + "\"}";
                string providerKey = "sk-proj-" + new string('a', 24) + "-" + new string('b', 24);
                string quotedAssignment = "password=\"abcdef\\\"" + suffix + "\"";
                string encodedArray = "{\"output\":[\"sk-\\u0070roj-" + new string('b', 24) + "\"]}";
                string encodedRoot = "\"sk-\\u0070roj-" + new string('b', 24) + "\"";
                foreach (string raw in new[] { escaped, encodedKey, providerKey, escaped.TrimEnd('}'), quotedAssignment, encodedArray, encodedRoot })
                {
                    string safe = RuntimeLogFormatter.RedactSecrets(raw);
                    AssertFalse(safe.Contains(suffix));
                    AssertFalse(safe.Contains(new string('b', 24)));
                    AssertEqual(safe, RuntimeLogFormatter.RedactSecrets(safe), "Redaction is idempotent");
                    FormattedLogLine fallback = RuntimeLogFormatter.Format(raw, AgentRuntimeEnum.Custom);
                    AssertFalse(fallback.Text.Contains(suffix));
                    AssertFalse(fallback.Text.Contains(new string('b', 24)));
                }
                string tool = "{\"eventType\":\"tool_call_proposed\",\"toolCall\":{\"name\":\"" + providerKey + "\"}}";
                AssertFalse(RuntimeLogFormatter.Format(tool, AgentRuntimeEnum.Mux).ToolName!.Contains(new string('b', 24)));
                string nested = System.Text.Json.JsonSerializer.Serialize(new { content = encodedKey });
                AssertFalse(RuntimeLogFormatter.RedactSecrets(nested).Contains(suffix));
            });

            await RunTest("Tool names use the same redaction as display text", () =>
            {
                string secret = "example-" + "secret-value";
                string json = "{\"eventType\":\"tool_call_proposed\",\"toolCall\":{\"name\":\"token=" + secret + "\"}}";
                FormattedLogLine line = RuntimeLogFormatter.Format(json, AgentRuntimeEnum.Mux);
                AssertTrue(line.IsToolCall);
                AssertTrue(line.Redacted);
                AssertFalse(line.Text.Contains(secret));
                AssertFalse(line.ToolName!.Contains(secret));
            });
            await RunTest("Malformed nested events return safe text instead of throwing", () =>
            {
                foreach (string json in new[]
                {
                    "{\"eventType\":\"tool_call_completed\",\"result\":\"wrong shape\"}",
                    "{\"eventType\":\"tool_call_proposed\",\"toolCall\":42}",
                    "{invalid json"
                })
                {
                    FormattedLogLine line = RuntimeLogFormatter.Format(json, AgentRuntimeEnum.Mux);
                    AssertFalse(line.Dropped);
                    AssertTrue(line.Text.Length > 0);
                }
            });
            await RunTest("Claude content tool use resolves its actual name", () =>
            {
                FormattedLogLine line = RuntimeLogFormatter.Format(
                    "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\"}]}}", AgentRuntimeEnum.ClaudeCode);
                AssertTrue(line.IsToolCall);
                AssertEqual("Read", line.ToolName);
                AssertContains("Read", line.Text);
            });
            await RunTest("Codex command items are tool events", () =>
            {
                FormattedLogLine line = RuntimeLogFormatter.Format(
                    "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"command\":\"git status\",\"status\":\"completed\"}}", AgentRuntimeEnum.Codex);
                AssertTrue(line.IsToolCall);
                AssertContains("git status", line.Text);
            });
            await RunTest("OpenCode reasoning remains visibly distinct", () =>
            {
                FormattedLogLine line = RuntimeLogFormatter.Format(
                    "{\"type\":\"reasoning\",\"part\":{\"text\":\"Check the evidence\"}}", AgentRuntimeEnum.OpenCode);
                AssertContains("(thinking)", line.Text);
                AssertContains("Check the evidence", line.Text);
                System.Collections.Generic.IReadOnlyList<FormattedLogLine> completed = RuntimeLogFormatter.FormatEntries(
                    "{\"type\":\"tool_use\",\"part\":{\"tool\":\"read\",\"state\":{\"status\":\"completed\",\"output\":\"observed output\"}}}", AgentRuntimeEnum.OpenCode);
                AssertEqual(2, completed.Count);
                AssertEqual(LogEntryKindEnum.ToolResult, completed[0].Kind);
                AssertEqual("observed output", completed[1].Text);
            });
            await RunTest("Mixed blocks retain distinct kinds and bounded pages", () =>
            {
                string json = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"inspect\"},{\"type\":\"text\",\"text\":\"observed\"},{\"type\":\"tool_use\",\"name\":\"Read\"}]}}";
                System.Collections.Generic.IReadOnlyList<FormattedLogLine> entries = RuntimeLogFormatter.FormatEntries(json, AgentRuntimeEnum.ClaudeCode);
                AssertEqual(3, entries.Count);
                AssertEqual(LogEntryKindEnum.Thinking, entries[0].Kind);
                AssertEqual(LogEntryKindEnum.Text, entries[1].Kind);
                AssertEqual(LogEntryKindEnum.ToolCall, entries[2].Kind);
                System.Collections.Generic.List<FormattedLogLine> page = RuntimeLogFormatter.FormatPage(
                    Enumerable.Repeat(json, 200), AgentRuntimeEnum.ClaudeCode, out bool truncated);
                AssertEqual(500, page.Count);
                AssertTrue(truncated);
                page = RuntimeLogFormatter.FormatPage(Enumerable.Repeat("text", 500), AgentRuntimeEnum.Custom, out truncated);
                AssertEqual(500, page.Count);
                AssertFalse(truncated);
                string many = "{\"type\":\"assistant\",\"content\":[" + String.Join(",", Enumerable.Repeat("{\"type\":\"text\",\"text\":\"block\"}", 20)) + "]}";
                entries = RuntimeLogFormatter.FormatEntries(many, AgentRuntimeEnum.ClaudeCode);
                AssertEqual(16, entries.Count);
                AssertTrue(entries[15].Truncated);
            });
            await RunTest("Tool result bodies and quoted credentials are redacted", () =>
            {
                string secret = "example-" + "secret-value";
                string raw = "{\"password\":\"" + secret + "\"}";
                AssertFalse(RuntimeLogFormatter.RedactSecrets(raw).Contains(secret));
                string json = "{\"type\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"password=" + secret + "\"}]}";
                FormattedLogLine line = RuntimeLogFormatter.Format(json, AgentRuntimeEnum.ClaudeCode);
                AssertEqual(LogEntryKindEnum.ToolResult, line.Kind);
                AssertTrue(line.Redacted);
                AssertFalse(line.Text.Contains(secret));
            });

            await RunTest("Display bounds and noise behavior remain compatible", () =>
            {
                AssertTrue(RuntimeLogFormatter.Format(null, AgentRuntimeEnum.Custom).Dropped);
                AssertTrue(RuntimeLogFormatter.Format("Determining projects to restore", AgentRuntimeEnum.Custom).Dropped);
                FormattedLogLine line = RuntimeLogFormatter.Format(new string('x', 5000), AgentRuntimeEnum.Custom);
                AssertTrue(line.Truncated);
                AssertTrue(line.Text.Length < 2100);
                AssertContains("[truncated", line.Text);
            });
        }
    }
}
