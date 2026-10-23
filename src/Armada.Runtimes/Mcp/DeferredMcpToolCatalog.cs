namespace Armada.Runtimes.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Runtimes.Tools;
    using PolyToolDefinition = PolyPrompt.Models.ToolDefinition;

    /// <summary>Exposes a bounded view of selected MCP tool schemas and adds revealed schemas to the next request.</summary>
    internal sealed class DeferredMcpToolCatalog
    {
        internal const string DiscoveryName = "armada_discover_mcp_tools";
        internal const int EagerCatalogLimit = 32;
        private const int _PageSize = 4;
        private const int _MaximumQueryLength = 256;
        private const int _MaximumDescriptionLength = 400;

        private readonly List<PolyToolDefinition> _Catalog;
        private readonly List<PolyToolDefinition> _RequestTools;
        private readonly HashSet<string> _RevealedNames = new HashSet<string>(StringComparer.Ordinal);

        internal DeferredMcpToolCatalog(List<PolyToolDefinition> catalog, List<PolyToolDefinition> requestTools)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (requestTools == null) throw new ArgumentNullException(nameof(requestTools));

            _Catalog = catalog.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
            _RequestTools = requestTools;
        }

        internal PolyToolDefinition DiscoveryToolDefinition => PolyToolDefinition.Function(
            DiscoveryName,
            "Search available Armada MCP tools by exact name or keywords. Use query='*' to browse all tools. Results show short descriptions and paging; full schemas are added to the next model request. This tool only discovers tools and does not run them.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["query"] = new
                    {
                        type = "string",
                        minLength = 1,
                        maxLength = _MaximumQueryLength,
                        description = "Exact tool name, search keywords, or * to browse all selected tools."
                    },
                    ["offset"] = new
                    {
                        type = "integer",
                        minimum = 0,
                        description = "Zero-based result offset for the next page."
                    }
                },
                ["required"] = new[] { "query" },
                ["additionalProperties"] = false
            });

        internal string Discover(string? argumentsJson)
        {
            DeferredMcpToolDiscoveryArguments request;
            string query;
            int offset;
            try
            {
                request = ToolArgumentParser.Parse<DeferredMcpToolDiscoveryArguments>(argumentsJson ?? "{}");
                query = (request.Query ?? String.Empty).Trim();
                if (String.IsNullOrWhiteSpace(query) || query.Length > _MaximumQueryLength)
                    throw new ArgumentException("query must contain 1 to 256 characters.");

                offset = request.Offset ?? 0;
                if (offset < 0) throw new ArgumentException("offset must be zero or greater.");
            }
            catch (ArgumentException)
            {
                return InvalidArgumentsResponse();
            }
            catch (ToolSizeLimitException)
            {
                return InvalidArgumentsResponse();
            }

            List<ScoredTool> matches = FindMatches(query);
            List<PolyToolDefinition> page = new List<PolyToolDefinition>();
            int end = (int)Math.Min(matches.Count, (long)offset + _PageSize);
            for (int index = offset; index < end; index++)
            {
                PolyToolDefinition definition = matches[index].Definition;
                if (_RevealedNames.Add(definition.Name)) _RequestTools.Add(definition);
                page.Add(definition);
            }

            int? nextOffset = end < matches.Count ? end : (int?)null;
            return JsonSerializer.Serialize(new
            {
                total = matches.Count,
                offset,
                nextOffset,
                tools = page.Select(tool => new { name = tool.Name, description = TruncateDescription(tool.Description) }),
                message = page.Count == 0
                    ? "No selected MCP tools matched this query."
                    : "The full schemas for these tools are available in the next model request."
            });
        }

        private List<ScoredTool> FindMatches(string query)
        {
            List<ScoredTool> matches = new List<ScoredTool>();
            bool browseAll = String.Equals(query.Trim(), "*", StringComparison.Ordinal);
            foreach (PolyToolDefinition definition in _Catalog)
            {
                int score = Score(definition, query);
                if (browseAll || score > 0) matches.Add(new ScoredTool(definition, score));
            }

            if (matches.Any(match => match.Score == Int32.MaxValue))
                matches = matches.Where(match => match.Score == Int32.MaxValue).ToList();
            if (browseAll)
                return matches.OrderBy(match => match.Definition.Name, StringComparer.Ordinal).ToList();

            matches.Sort((left, right) =>
            {
                int scoreOrder = right.Score.CompareTo(left.Score);
                return scoreOrder != 0
                    ? scoreOrder
                    : StringComparer.Ordinal.Compare(left.Definition.Name, right.Definition.Name);
            });
            return matches;
        }

        private static int Score(PolyToolDefinition definition, string query)
        {
            if (String.Equals(definition.Name, query.Trim(), StringComparison.OrdinalIgnoreCase)) return Int32.MaxValue;

            string[] terms = query.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            int score = 0;
            foreach (string term in terms)
            {
                if (definition.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 3;
                if ((definition.Description ?? String.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score++;
            }
            return score;
        }

        private static string TruncateDescription(string? description)
        {
            string value = description ?? String.Empty;
            if (value.Length <= _MaximumDescriptionLength) return value;
            return value.Substring(0, _MaximumDescriptionLength - 1) + "…";
        }

        private static string InvalidArgumentsResponse()
        {
            return JsonSerializer.Serialize(new
            {
                error = "invalid_arguments",
                message = "query must contain 1 to 256 characters and offset must be zero or greater."
            });
        }

        private sealed class ScoredTool
        {
            internal ScoredTool(PolyToolDefinition definition, int score)
            {
                Definition = definition;
                Score = score;
            }

            internal PolyToolDefinition Definition { get; }

            internal int Score { get; }
        }
        private sealed class DeferredMcpToolDiscoveryArguments
        {
            /// <summary>Create a discovery request for typed JSON deserialization.</summary>
            public DeferredMcpToolDiscoveryArguments() { }

            /// <summary>Search text or the wildcard browse marker.</summary>
            [JsonPropertyName("query")]
            public string? Query { get; set; }

            /// <summary>Optional nonnegative result offset.</summary>
            [JsonPropertyName("offset")]
            public int? Offset { get; set; }
        }

    }
}
