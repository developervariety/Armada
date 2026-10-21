namespace Armada.Runtimes.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using Armada.Runtimes.Tools;
    using PolyPrompt.Models;

    /// <summary>
    /// Loads a large, already policy-filtered MCP catalog into model requests a few schemas at a time.
    /// Discovery changes exposure only: authorization and execution still use the original scoped MCP client.
    /// </summary>
    internal sealed class DeferredMcpToolCatalog
    {
        internal const string DiscoveryName = "armada_discover_mcp_tools";
        internal const int EagerCatalogLimit = 32;
        private const int PageSize = 4;
        private readonly List<PolyPrompt.Models.ToolDefinition> _Catalog;
        private readonly List<PolyPrompt.Models.ToolDefinition> _Exposed;
        private readonly HashSet<string> _Loaded = new HashSet<string>(StringComparer.Ordinal);

        internal DeferredMcpToolCatalog(List<PolyPrompt.Models.ToolDefinition> catalog, List<PolyPrompt.Models.ToolDefinition> exposed)
        {
            _Catalog = catalog.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            _Exposed = exposed;
        }

        internal static PolyPrompt.Models.ToolDefinition Definition()
        {
            return PolyPrompt.Models.ToolDefinition.Function(DiscoveryName,
                "Find additional Armada MCP tools by exact name or a few task keywords (e.g. mission log, memory, status). "
                + "Matching tool schemas become available in your next turn. This only discovers tools; it executes no remote action. "
                + "Use query '*' to browse, and offset for the next page.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", minLength = 1, maxLength = 256 },
                        ["offset"] = new { type = "integer", minimum = 0 }
                    },
                    ["required"] = new[] { "query" },
                    ["additionalProperties"] = false
                });
        }

        internal ToolResult Discover(string callId, JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object
                || !arguments.TryGetProperty("query", out JsonElement queryElement)
                || queryElement.ValueKind != JsonValueKind.String)
                return Invalid(callId);
            string query = queryElement.GetString()!.Trim();
            int offset = 0;
            if (String.IsNullOrEmpty(query) || query.Length > 256
                || (arguments.TryGetProperty("offset", out JsonElement offsetElement)
                    && (offsetElement.ValueKind != JsonValueKind.Number || !offsetElement.TryGetInt32(out offset) || offset < 0)))
                return Invalid(callId);

            string[] words = query.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            bool exact = _Catalog.Any(t => String.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase));
            var matches = _Catalog.Select(t => new
                {
                    Tool = t,
                    Score = String.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase) ? Int32.MaxValue
                        : words.Sum(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ? 3
                            : (t.Description ?? String.Empty).Contains(w, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                })
                .Where(t => query == "*" || (exact ? t.Score == Int32.MaxValue : t.Score > 0))
                .OrderByDescending(t => t.Score).ThenBy(t => t.Tool.Name, StringComparer.Ordinal)
                .ToList();
            var page = matches.Skip(offset).Take(PageSize).Select(t => t.Tool).ToList();
            foreach (PolyPrompt.Models.ToolDefinition tool in page)
                if (_Loaded.Add(tool.Name)) _Exposed.Add(tool);

            return new ToolResult
            {
                ToolCallId = callId,
                Content = JsonSerializer.Serialize(new
                {
                    total = matches.Count,
                    offset,
                    nextOffset = (long)offset + page.Count < matches.Count ? (int?)(offset + page.Count) : null,
                    tools = page.Select(t => new { name = t.Name, description = (t.Description ?? String.Empty).Substring(0, Math.Min(400, (t.Description ?? String.Empty).Length)) }),
                    message = page.Count == 0 ? "No matching tools. Try a shorter keyword or query '*' to browse."
                        : "These tool schemas are now loaded. Call the required tool in your next turn."
                })
            };
        }

        private static ToolResult Invalid(string callId)
        {
            return new ToolResult { ToolCallId = callId, Success = false,
                Content = "{\"error\":\"invalid_arguments\",\"message\":\"Supply a nonblank query of at most 256 characters and an optional nonnegative integer offset.\"}" };
        }
    }
}
