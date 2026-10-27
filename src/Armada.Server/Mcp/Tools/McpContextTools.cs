namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services;
    using Armada.Server.Mcp;
    using SyslogLogging;

    /// <summary>
    /// Registers the captain-facing context fetch tool <c>armada_fetch_context</c>. It is
    /// mission-scoped, read-only, and informative: given a topic or short query (and the caller's
    /// mission id), it runs the deterministic <see cref="ContextRetrievalService"/> and returns the
    /// relevant LEAF chunk bodies for that query. The core bundle already ships inline in the brief,
    /// so this tool returns only leaves and the matching-domain must-retrieve safety leaves.
    ///
    /// Authority does not travel with the tool. It dispatches nothing, lands nothing, and changes no
    /// source content. It records shared mission recall usage. It never persists
    /// the raw request query — only its length and a short hash reach a log. Its content is
    /// already-sanitized AI-Memory and docs text, so bodies pass through. It is bounded by a
    /// per-mission call budget from <c>contextRetrieval</c> settings; when the budget is spent it
    /// returns a clear budget message. Usage is stored on the mission and survives restart.
    /// </summary>
    public static class McpContextTools
    {
        #region Public-Members

        /// <summary>Registered name of the context fetch tool.</summary>
        public const string FetchContextToolName = "armada_fetch_context";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the context fetch tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="retrieval">The retrieval service over the built context index, or null when
        /// the host does not have a built index.</param>
        /// <param name="database">Database driver, for resolving the calling mission's vessel and persona.</param>
        /// <param name="settings">Armada settings supplying <c>contextRetrieval</c>.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="participantKeyProvider">Returns the calling captain's participant key, or null.
        /// Retained for source compatibility; operator calls have no mission counter.</param>
        /// <param name="sharedBudget">Shared persistent recall budget.</param>
        public static void Register(
            RegisterToolDelegate register,
            ContextRetrievalService? retrieval,
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule? logging = null,
            Func<string?>? participantKeyProvider = null,
            ContextRecallBudget? sharedBudget = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            ContextRecallBudget budget = sharedBudget ?? new ContextRecallBudget(database, settings);

            register(
                FetchContextToolName,
                "Compatibility guidance-only query. Prefer armada_search_context for task recall across guidance, facts and code. Fetch the memory and docs context relevant to a topic or short task, on demand. Your brief already carries the always-on core rules; this tool returns the relevant LEAF chunks for what you name, so you can pull a procedure or a rule you need without carrying all of memory. Give a 'query' (a short task description) and/or a 'topic', and pass your mission id in 'missionId' so the tool scopes the result to your vessel and persona. It is read-only: it returns text you weigh, dispatches nothing, edits no record, and writes no memory. Its content is already sanitized. It shares the durable mission recall budget with the other recall tools; when the budget is spent it says so. Safety leaves for your vessel are always included.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "A short task description to rank the leaves against. Optional if you pass a topic." },
                        topic = new { type = "string", description = "An exact topic id, or a comma-separated list of topics, to prefer. Optional if you pass a query." },
                        missionId = new { type = "string", description = "The calling mission id, used to resolve your vessel and persona and to scope the per-mission budget. Optional." }
                    }
                },
                async (args) =>
                {
                    McpCallerContext.Require();
                    if (retrieval == null) return McpToolUnavailable.Create("context retrieval");
                    return await HandleAsync(args, retrieval, database, settings, logging, budget).ConfigureAwait(false);
                });
        }

        #endregion

        #region Private-Methods

        private static async Task<object> HandleAsync(
            JsonElement? args,
            ContextRetrievalService retrieval,
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule? logging,
            ContextRecallBudget budget)
        {
            try
            {
                if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object)
                    return Unavailable("invalid", "The call carried no arguments object.");

                ContextFetchArgs requestArgs = JsonSerializer.Deserialize<ContextFetchArgs>(args.Value, _JsonOptions) ?? new ContextFetchArgs();
                string? query = requestArgs.Query;
                string? topicArg = requestArgs.Topic;
                if (String.IsNullOrWhiteSpace(query) && String.IsNullOrWhiteSpace(topicArg))
                    return Unavailable("invalid", "Give a query or a topic to fetch context for.");

                ContextRetrievalSettings toolSettings = settings.ContextRetrieval;
                if (!toolSettings.FetchToolEnabled)
                    return Unavailable("disabled", "The context fetch tool is not enabled.");

                ContextRecallScope scope = await ContextRecallScope.ResolveAsync(database, McpCallerContext.Require(),
                    null, requestArgs.MissionId, false, McpCallerContext.CallToken).ConfigureAwait(false);
                int? consumed = await budget.ConsumeAsync(scope, McpCallerContext.CallToken).ConfigureAwait(false);
                if (!consumed.HasValue) return BudgetExhausted(budget.Maximum, budget.Maximum);
                int used = consumed.Value;

                ContextRetrievalRequest request = new ContextRetrievalRequest
                {
                    Query = query,
                    Topics = ParseTopics(topicArg),
                    RequestingPersona = scope.Mission?.Persona,
                    Vessel = scope.Vessel?.Name,
                    MaxLeafBytes = toolSettings.MaxLeafBytesPerCall
                };

                ContextRetrievalResult result = retrieval.Retrieve(request);

                // Read-only: never persist the raw query. Only its length and a short hash reach a log.
                logging?.Debug("[McpContextTools] fetch qlen=" +
                    (query?.Length ?? 0).ToString(CultureInfo.InvariantCulture) +
                    " qhash=" + ShortHash(query ?? topicArg ?? "") +
                    " leaves=" + result.Leaves.Count +
                    " must=" + result.MustRetrieve.Count +
                    " leafBytes=" + result.LeafBytes +
                    (result.Degraded ? " degraded=1" : ""));

                return BuildAnswer(result, used, budget.Maximum);
            }
            catch (OperationCanceledException) { throw; }
            catch (KeyNotFoundException) { return Unavailable("not_found", "Mission or vessel not found in the caller's scope."); }
            catch (UnauthorizedAccessException) { return Unavailable("forbidden", "An active owned mission is required."); }
            catch (JsonException) { return Unavailable("invalid", "The query arguments are invalid."); }
            catch (Exception ex)
            {
                // The tool must never throw into the captain's runtime.
                logging?.Warn("[McpContextTools] fetch failed: " + ex.Message);
                return Unavailable("exception", "The context fetch tool encountered an error. Read the memory by hand.");
            }
        }

        private static List<string> ParseTopics(string? topicArg)
        {
            List<string> topics = new List<string>();
            if (String.IsNullOrWhiteSpace(topicArg)) return topics;
            foreach (string part in topicArg!.Split(','))
            {
                string t = part.Trim();
                if (t.Length > 0) topics.Add(t);
            }
            return topics;
        }

        private static object BuildAnswer(ContextRetrievalResult result, int callsUsed, int maxCalls)
        {
            List<object> leaves = new List<object>();
            foreach (ContextChunk c in result.MustRetrieve) leaves.Add(Project(c, true));
            foreach (ContextChunk c in result.Leaves) leaves.Add(Project(c, false));

            return new
            {
                Available = true,
                Degraded = result.Degraded,
                Note = result.Note,
                LeafCount = result.MustRetrieve.Count + result.Leaves.Count,
                MustRetrieveCount = result.MustRetrieve.Count,
                LeafBytes = result.LeafBytes,
                Leaves = leaves,
                CallsUsed = callsUsed,
                MaxCallsPerMission = maxCalls,
                Note_Core = "The always-on core rules already ship in your brief; this tool returns only the relevant leaves."
            };
        }

        private static object Project(ContextChunk c, bool mustRetrieve)
        {
            return new
            {
                Topic = c.Topic,
                Path = c.Path,
                Summary = c.Summary,
                ReadWhen = c.ReadWhen,
                MustRetrieve = mustRetrieve,
                Bytes = c.Bytes,
                Text = c.Text
            };
        }

        private static object Unavailable(string reason, string message)
        {
            return new { Available = false, UnavailableReason = reason, Message = message };
        }

        private static object BudgetExhausted(int used, int max)
        {
            return new
            {
                Available = false,
                UnavailableReason = "budget",
                Message = "This mission has used its context fetch budget. Read the memory by hand at the path the manifest gives.",
                CallsUsed = used,
                MaxCallsPerMission = max
            };
        }

        private static string ShortHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? String.Empty));
                StringBuilder sb = new StringBuilder(12);
                for (int i = 0; i < 6 && i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private sealed class ContextFetchArgs
        {
            public string? Query { get; set; }
            public string? Topic { get; set; }
            public string? MissionId { get; set; }
        }

        #endregion
    }
}
