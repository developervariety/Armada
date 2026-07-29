namespace Armada.Runtimes
{
    using System.Text.Json;

    /// <summary>
    /// Extracts safe, useful activity from structured runtime events.
    /// </summary>
    internal static class StructuredRuntimeLogFormatter
    {
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
            return value.Length <= maximumLength
                ? value
                : value.Substring(0, maximumLength) + "...";
        }
    }
}
