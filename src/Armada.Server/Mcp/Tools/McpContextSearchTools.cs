namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>Registers the shared, caller-scoped context query.</summary>
    public static class McpContextSearchTools
    {
        /// <summary>Name shared by tool registration and access policy.</summary>
        public const string ToolName = "armada_search_context";

        private static readonly ConcurrentDictionary<string, int> _CallsByMission = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>Register the local retrieval tool without adding a write or operator capability.</summary>
        /// <param name="register">Transport registration delegate.</param>
        /// <param name="search">Shared service over the existing stores.</param>
        /// <param name="settings">Existing context call and byte limits.</param>
        public static void Register(RegisterToolDelegate register, ContextSearchService search, ContextRetrievalSettings settings)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (search == null) throw new ArgumentNullException(nameof(search));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            register(ToolName,
                "Search applicable rules, native working facts and existing vessel code in one local query. Required core and safety guidance is returned in full, outside optional result limits. Results name their source and version; working facts never override rules. Your mission credential fixes your vessel. Operators supply vesselId. This read-only tool makes no embedding call and never creates an index. Check Sources for missing or failed sources; an unavailable source does not prove content is absent.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Task text or identifier, up to 4096 characters" },
                        vesselId = new { type = "string", description = "Operator vessel scope; ignored for mission credentials" },
                        limit = new { type = "integer", description = "Maximum optional results, 1 through 40" },
                        maxBytes = new { type = "integer", description = "Optional text byte budget, capped by the server setting" },
                        includeCode = new { type = "boolean", description = "Search the existing local code index; default true" }
                    },
                    required = new[] { "query" }
                },
                async args =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!settings.FetchToolEnabled) return Failure("disabled", "Context retrieval is disabled.");
                    try
                    {
                        ContextSearchRequest request = args.HasValue
                            ? JsonSerializer.Deserialize<ContextSearchRequest>(args.Value, _JsonOptions) ?? new ContextSearchRequest()
                            : new ContextSearchRequest();
                        if (String.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 4096)
                            return Failure("invalid", "query must contain 1 through 4096 characters.");
                        if (!String.IsNullOrWhiteSpace(caller.MissionId))
                        {
                            int calls = _CallsByMission.AddOrUpdate(caller.MissionId, 1, (_, previous) => previous + 1);
                            if (calls > settings.MaxCallsPerMission) return Failure("budget", "The mission has used its unified context search budget.");
                        }
                        request.MaxBytes = Math.Clamp(request.MaxBytes, 0, settings.MaxLeafBytesPerCall);
                        return (object)await search.SearchAsync(caller, request, McpCallerContext.CallToken).ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException)
                    {
                        return Failure("not_found", "Vessel not found in the caller's scope.");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return Failure("forbidden", "The caller has no active owned mission scope.");
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
                    {
                        return Failure("invalid", "The query arguments are invalid.");
                    }
                });
        }

        private static object Failure(string reason, string message)
        {
            return new { Available = false, UnavailableReason = reason, Message = message };
        }
    }
}
