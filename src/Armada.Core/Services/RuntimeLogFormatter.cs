namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Turns a raw captain-runtime log line into something readable: resolves the real tool name out of a
    /// runtime's nested JSON event, redacts secret-shaped values, truncates oversized payloads with a
    /// marker, and drops known-noise lines. Pure (no I/O) so it can be unit tested against captured
    /// fixtures and slotted into the runtime output handlers and the log read-back paths without changing
    /// where logs are stored.
    /// </summary>
    public static class RuntimeLogFormatter
    {
        #region Private-Members

        private const int _MaxLineChars = 2000;

        // Secret-shaped values to redact. Order matters only in that each is applied independently.
        private static readonly (Regex Pattern, string Replacement)[] _Redactions = new (Regex, string)[]
        {
            (new Regex(@"(?i)\bBearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.Compiled), "Bearer [REDACTED]"),
            (new Regex(@"\bsk-[A-Za-z0-9]{16,}", RegexOptions.Compiled), "sk-[REDACTED]"),
            (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "gh_[REDACTED]"),
            (new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled), "AKIA[REDACTED]"),
            (new Regex(@"(?i)(api[_\-]?key|apikey|secret|token|password)(\s*[=:]\s*)([""']?)[^\s""']{6,}", RegexOptions.Compiled), "$1$2$3[REDACTED]"),
        };

        // Lines that are pure noise and should not clutter the readable view.
        private static readonly Regex _NoiseLine = new Regex(@"^\s*(\[dotnet\]|Determining projects to restore|Restored\s|MSBuild version|Welcome to \.NET)", RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Format one raw log line for a given runtime.
        /// </summary>
        /// <param name="rawLine">The raw line as captured from the runtime's stdout/stderr.</param>
        /// <param name="runtime">The runtime that produced the line (informational; JSONL handling is shared).</param>
        /// <returns>The formatted line; never null.</returns>
        public static FormattedLogLine Format(string? rawLine, Armada.Core.Enums.AgentRuntimeEnum runtime)
        {
            FormattedLogLine result = new FormattedLogLine();

            if (rawLine == null) { result.Dropped = true; return result; }

            string line = rawLine.TrimEnd('\r', '\n');
            if (String.IsNullOrWhiteSpace(line)) { result.Dropped = true; return result; }
            if (_NoiseLine.IsMatch(line)) { result.Dropped = true; return result; }

            // Structured JSONL event (Mux / OpenCode style): resolve a readable tool-call summary.
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) && TryFormatJsonEvent(trimmed, result))
            {
                ApplyRedactionAndTruncation(result);
                return result;
            }

            result.Text = line;
            ApplyRedactionAndTruncation(result);
            return result;
        }

        #endregion

        #region Private-Methods

        private static bool TryFormatJsonEvent(string json, FormattedLogLine result)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                string eventType = root.TryGetProperty("eventType", out JsonElement et) && et.ValueKind == JsonValueKind.String
                    ? et.GetString() ?? "" : "";

                if (eventType == "tool_call_proposed" && root.TryGetProperty("toolCall", out JsonElement tc))
                {
                    string? name = tc.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    result.IsToolCall = true;
                    result.ToolName = name;
                    result.Text = "-> tool " + (name ?? "unknown");
                    return true;
                }

                if (eventType == "tool_call_completed")
                {
                    string? name = root.TryGetProperty("toolName", out JsonElement tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() : null;
                    bool ok = true;
                    if (root.TryGetProperty("result", out JsonElement res) && res.TryGetProperty("success", out JsonElement suc)
                        && (suc.ValueKind == JsonValueKind.True || suc.ValueKind == JsonValueKind.False))
                        ok = suc.GetBoolean();
                    result.IsToolCall = true;
                    result.ToolName = name;
                    result.Text = "<- tool " + (name ?? "unknown") + (ok ? " ok" : " failed");
                    return true;
                }

                if (eventType == "assistant_text" && root.TryGetProperty("text", out JsonElement txt) && txt.ValueKind == JsonValueKind.String)
                {
                    result.Text = txt.GetString() ?? "";
                    return true;
                }
            }
            catch (JsonException)
            {
            }

            return false;
        }

        private static void ApplyRedactionAndTruncation(FormattedLogLine result)
        {
            string text = result.Text;
            foreach ((Regex pattern, string replacement) in _Redactions)
            {
                string next = pattern.Replace(text, replacement);
                if (!ReferenceEquals(next, text) && next != text) result.Redacted = true;
                text = next;
            }

            if (text.Length > _MaxLineChars)
            {
                text = text.Substring(0, _MaxLineChars) + " ... [truncated " + (text.Length - _MaxLineChars) + " chars]";
                result.Truncated = true;
            }

            result.Text = text;
        }

        #endregion
    }
}
