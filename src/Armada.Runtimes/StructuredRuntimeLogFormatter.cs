namespace Armada.Runtimes
{
    using System.Text.Json;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Extracts safe, useful activity from structured runtime events.
    /// </summary>
    internal static class StructuredRuntimeLogFormatter
    {
        /// <summary>
        /// Maximum rendered length of a tool name in an activity record.
        /// </summary>
        public const int ToolNameLimit = 80;

        /// <summary>
        /// Maximum rendered length of a shell command in an activity record.
        /// </summary>
        public const int CommandDetailLimit = 240;

        /// <summary>
        /// Maximum rendered length of a path, pattern, or query in an activity record.
        /// </summary>
        public const int ShortDetailLimit = 160;

        private static readonly string[] _ToolNameProperties =
        {
            "tool_name",
            "toolName",
            "name",
            "tool"
        };

        /// <summary>
        /// Try to render a named tool event without copying tool input or output into the log.
        /// </summary>
        public static bool TryBuildToolActivity(string line, out string activity)
        {
            activity = String.Empty;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!ContainsToolMarker(root, 0))
                    return false;

                string? toolName = FindToolName(root, 0);
                if (String.IsNullOrWhiteSpace(toolName))
                    return false;

                activity = "[ARMADA:ACTIVITY] tool " + Truncate(toolName.Trim(), 80);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Build a compact, redacted tool activity record for the shared mission-log timeline.
        /// Tool output is deliberately excluded because it is potentially large and sensitive.
        /// </summary>
        /// <param name="toolName">Tool name reported by the runtime.</param>
        /// <param name="detail">Optional primary argument (file path, command, pattern, query).</param>
        /// <param name="status">Optional status or outcome word.</param>
        /// <returns>Activity record, or an empty string when the tool name is missing.</returns>
        public static string BuildToolActivity(string? toolName, string? detail, string? status)
        {
            if (String.IsNullOrWhiteSpace(toolName))
                return String.Empty;

            string rendered = "[ARMADA:ACTIVITY] tool " + Truncate(toolName.Trim(), ToolNameLimit);

            if (!String.IsNullOrWhiteSpace(detail))
                rendered += " " + RedactSecretValues(detail.Trim());

            if (!String.IsNullOrWhiteSpace(status))
                rendered += " (" + Truncate(status.Trim(), 40) + ")";

            return rendered;
        }

        /// <summary>
        /// Bound activity text before it reaches durable telemetry.
        /// </summary>
        /// <param name="value">Text to bound.</param>
        /// <param name="maximumLength">Maximum length before truncation.</param>
        /// <returns>Bounded text.</returns>
        public static string TruncateActivityText(string value, int maximumLength)
        {
            return Truncate(value, maximumLength);
        }

        /// <summary>
        /// Redact token/password/key-shaped material while preserving structural text.
        /// </summary>
        /// <param name="value">Text to redact.</param>
        /// <returns>Redacted text.</returns>
        public static string RedactSecretValues(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            string redacted = Regex.Replace(
                value,
                "(?i)(password|token|secret|seed|private[_-]?key|api[_-]?key)\\s*[:=]\\s*([^\\s,;]+)",
                RedactNamedSecret);

            redacted = Regex.Replace(
                redacted,
                "-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
                RedactMatchedSecret,
                RegexOptions.Singleline);

            redacted = Regex.Replace(
                redacted,
                "\\b[0-9a-fA-F]{32,}\\b",
                RedactMatchedSecret);

            redacted = Regex.Replace(
                redacted,
                "\\b[A-Za-z0-9+/]{40,}={0,2}\\b",
                RedactMatchedSecret);

            return redacted;
        }

        /// <summary>
        /// Regex evaluator for key=value secret values.
        /// </summary>
        private static string RedactNamedSecret(Match match)
        {
            return match.Groups[1].Value + "=<redacted len=" + match.Groups[2].Value.Length + ">";
        }

        /// <summary>
        /// Regex evaluator for standalone secret-looking values.
        /// </summary>
        private static string RedactMatchedSecret(Match match)
        {
            return "<redacted len=" + match.Value.Length + ">";
        }

        private static bool ContainsToolMarker(JsonElement element, int depth)
        {
            if (depth > 5)
                return false;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if ((String.Equals(property.Name, "type", StringComparison.OrdinalIgnoreCase)
                         || String.Equals(property.Name, "eventType", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString()?.Contains("tool", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }

                    if (ContainsToolMarker(property.Value, depth + 1))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (ContainsToolMarker(item, depth + 1))
                        return true;
                }
            }

            return false;
        }

        private static string? FindToolName(JsonElement element, int depth)
        {
            if (depth > 5)
                return null;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (string propertyName in _ToolNameProperties)
                {
                    if (element.TryGetProperty(propertyName, out JsonElement value)
                        && value.ValueKind == JsonValueKind.String
                        && !String.IsNullOrWhiteSpace(value.GetString())
                        && !IsGenericToolLabel(value.GetString()!))
                    {
                        return value.GetString();
                    }
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string? nested = FindToolName(property.Value, depth + 1);
                    if (!String.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? nested = FindToolName(item, depth + 1);
                    if (!String.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }

            return null;
        }

        private static bool IsGenericToolLabel(string value)
        {
            string normalized = value.Replace('_', '-').Trim();
            return String.Equals(normalized, "tool", StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized, "tool-call", StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized, "tool-use", StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized, "tool-result", StringComparison.OrdinalIgnoreCase);
        }

        private static string Truncate(string value, int maximumLength)
        {
            return String.IsNullOrEmpty(value) || value.Length <= maximumLength
                ? value
                : value.Substring(0, maximumLength) + "...";
        }
    }
}
