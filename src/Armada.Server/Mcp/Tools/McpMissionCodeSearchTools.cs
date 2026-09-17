namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Registers the captain-facing code search tool <c>armada_mission_code_search</c>. It searches the
    /// code index of the calling mission's own vessel and no other: the vessel is resolved from the
    /// mission id, so the tool takes no vessel or fleet argument. It is read-only, writes no Armada
    /// record, and is bounded by a per-mission call budget from <c>codeIndex</c> settings.
    ///
    /// The index covers the vessel's default branch, not the captain's dock branch. When the index is
    /// missing, failed, or stale, or searches lexically only, the answer says so, because an empty result
    /// from a missing index must never read as "the code is absent".
    /// </summary>
    public static class McpMissionCodeSearchTools
    {
        #region Public-Members

        /// <summary>Registered name of the captain code search tool.</summary>
        public const string ToolName = "armada_mission_code_search";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Process-local per-mission call budget keyed by mission id. Reset on an admiral restart,
        // which ends every running captain.
        private static readonly ConcurrentDictionary<string, int> _CallsByMission =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the captain code search tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="codeIndex">Code index service.</param>
        /// <param name="database">Database driver, for resolving the calling mission's vessel.</param>
        /// <param name="settings">Armada settings supplying <c>codeIndex</c>.</param>
        /// <param name="logging">Optional logging module.</param>
        public static void Register(
            RegisterToolDelegate register,
            ICodeIndexService codeIndex,
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule? logging = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (codeIndex == null) throw new ArgumentNullException(nameof(codeIndex));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            register(
                ToolName,
                "Search the code index of your own mission's vessel. Use it to find existing code before you write new code, and to check whether a method you added duplicates one that already exists. Pass your mission id in 'missionId'; the tool resolves your vessel from it and searches no other vessel. The index covers the vessel's default branch, not your dock branch, so your own uncommitted changes are not in it. Results give path, line range, language, score, and an excerpt; set 'includeContent' for the full chunk. When the index is missing, failed, stale, or lexical-only, the answer says so: an empty result then does not mean the code is absent, so search the checkout by hand. It is read-only, writes no record, and is budgeted per mission.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Your mission id (msn_ prefix). Required; it selects the vessel to search and scopes the budget." },
                        query = new { type = "string", description = "Search query: identifiers, a short description, or a code body to find similar code." },
                        limit = new { type = "integer", description = "Maximum results (default and maximum from settings, normally 10)." },
                        pathPrefix = new { type = "string", description = "Optional repo-relative path prefix filter." },
                        language = new { type = "string", description = "Optional language filter, e.g. csharp or markdown." },
                        includeContent = new { type = "boolean", description = "Include the full chunk content in each result (default false)." }
                    },
                    required = new[] { "missionId", "query" }
                },
                (args) => HandleAsync(args, codeIndex, database, settings, logging));
        }

        /// <summary>
        /// Reset the process-local per-mission call budget. For tests only; production resets it on an
        /// admiral restart.
        /// </summary>
        public static void ResetBudgetForTests()
        {
            _CallsByMission.Clear();
        }

        #endregion

        #region Private-Methods

        private static async Task<object> HandleAsync(
            JsonElement? args,
            ICodeIndexService codeIndex,
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule? logging)
        {
            try
            {
                if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object)
                    return Unavailable("invalid", "The call carried no arguments object.");

                MissionCodeSearchArgs request = JsonSerializer.Deserialize<MissionCodeSearchArgs>(args.Value, _JsonOptions)
                    ?? new MissionCodeSearchArgs();
                if (String.IsNullOrWhiteSpace(request.MissionId))
                    return Unavailable("invalid", "Pass your mission id in 'missionId'.");
                if (String.IsNullOrWhiteSpace(request.Query))
                    return Unavailable("invalid", "Give a query to search for.");

                CodeIndexSettings indexSettings = settings.CodeIndex;
                if (!indexSettings.Enabled)
                    return Unavailable("disabled", "Code indexing is disabled on this admiral. Search the checkout by hand.");

                Mission? mission = await database.Missions.ReadAsync(request.MissionId, CancellationToken.None).ConfigureAwait(false);
                if (mission == null)
                    return Unavailable("mission_not_found", "No mission has that id.");

                // The endpoint has no per-captain authorization, so scope is the guard: the mission must
                // belong to the caller's tenant and still be live, and its vessel is the only one searched.
                AuthContext? caller = McpCallerContext.Current;
                if (caller != null && !caller.IsAdmin
                    && !String.Equals(caller.TenantId, mission.TenantId, StringComparison.Ordinal))
                    return Unavailable("mission_not_found", "No mission has that id.");
                if (MissionStateMachine.IsTerminal(mission.Status))
                    return Unavailable("mission_not_active", "That mission is no longer active, so it has no vessel search scope.");
                if (String.IsNullOrWhiteSpace(mission.VesselId))
                    return Unavailable("no_vessel", "That mission has no vessel to search.");

                int used = _CallsByMission.AddOrUpdate(mission.Id, 1, (_, previous) => previous + 1);
                if (used > indexSettings.CaptainSearchMaxCallsPerMission)
                {
                    return new
                    {
                        Available = false,
                        UnavailableReason = "budget",
                        Message = "This mission has used its code search budget. Search the checkout by hand.",
                        CallsUsed = used - 1,
                        MaxCallsPerMission = indexSettings.CaptainSearchMaxCallsPerMission
                    };
                }

                CodeIndexStatus status = await codeIndex.GetStatusAsync(mission.VesselId!).ConfigureAwait(false);
                if (!IsSearchable(status.Freshness))
                {
                    return new
                    {
                        Available = false,
                        UnavailableReason = "index_" + status.Freshness.ToLowerInvariant(),
                        Message = "The code index for your vessel is " + status.Freshness + ". No search ran, so nothing here says the code is absent. Search the checkout by hand.",
                        CallsUsed = used,
                        MaxCallsPerMission = indexSettings.CaptainSearchMaxCallsPerMission
                    };
                }

                int maxResults = indexSettings.CaptainSearchMaxResults;
                int limit = request.Limit <= 0 ? maxResults : Math.Min(request.Limit, maxResults);
                CodeSearchResponse response = await codeIndex.SearchAsync(new CodeSearchRequest
                {
                    VesselId = mission.VesselId!,
                    Query = request.Query,
                    Limit = limit,
                    PathPrefix = request.PathPrefix,
                    Language = request.Language,
                    IncludeContent = request.IncludeContent,
                    IncludeReferenceOnly = false
                }).ConfigureAwait(false);

                List<string> warnings = new List<string>();
                if (!String.Equals(status.Freshness, "Fresh", StringComparison.OrdinalIgnoreCase))
                    warnings.Add("The index is " + status.Freshness + ": it was built at " + (status.IndexedCommitSha ?? "an unknown commit") + ", so recent code may be missing from the results.");
                if (!status.UseSemanticSearch)
                    warnings.Add("The index is lexical only: results match words, so similar code with different names is not found.");
                warnings.Add("The index covers the default branch, not your dock branch.");

                return new
                {
                    Available = true,
                    VesselId = mission.VesselId,
                    Index = new
                    {
                        status.Freshness,
                        status.IndexedCommitSha,
                        SemanticSearch = status.UseSemanticSearch
                    },
                    Results = response.Results.Select(r => new
                    {
                        r.Score,
                        r.Record.Path,
                        r.Record.StartLine,
                        r.Record.EndLine,
                        r.Record.Language,
                        r.Excerpt,
                        Content = request.IncludeContent ? r.Record.Content : null
                    }).ToList(),
                    Warnings = warnings,
                    CallsUsed = used,
                    MaxCallsPerMission = indexSettings.CaptainSearchMaxCallsPerMission
                };
            }
            catch (Exception ex)
            {
                // The tool must never throw into the captain's runtime.
                logging?.Warn("[McpMissionCodeSearchTools] search failed: " + ex.Message);
                return Unavailable("exception", "The code search failed. Search the checkout by hand.");
            }
        }

        private static bool IsSearchable(string freshness)
        {
            return String.Equals(freshness, "Fresh", StringComparison.OrdinalIgnoreCase)
                || String.Equals(freshness, "Stale", StringComparison.OrdinalIgnoreCase)
                || String.Equals(freshness, "Updating", StringComparison.OrdinalIgnoreCase);
        }

        private static object Unavailable(string reason, string message)
        {
            return new { Available = false, UnavailableReason = reason, Message = message };
        }

        #endregion

        #region Private-Classes

        private sealed class MissionCodeSearchArgs
        {
            public string MissionId { get; set; } = "";

            public string Query { get; set; } = "";

            public int Limit { get; set; } = 0;

            public string? PathPrefix { get; set; } = null;

            public string? Language { get; set; } = null;

            public bool IncludeContent { get; set; } = false;
        }

        #endregion
    }
}
