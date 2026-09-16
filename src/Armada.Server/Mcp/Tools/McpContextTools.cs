namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Concurrent;
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
    using SyslogLogging;

    /// <summary>
    /// Registers the captain-facing context fetch tool <c>armada_fetch_context</c>. It is
    /// mission-scoped, read-only, and informative: given a topic or short query (and the caller's
    /// mission id), it runs the deterministic <see cref="ContextRetrievalService"/> and returns the
    /// relevant LEAF chunk bodies for that query. The core bundle already ships inline in the brief,
    /// so this tool returns only leaves and the matching-domain must-retrieve safety leaves.
    ///
    /// Authority does not travel with the tool. It writes NOTHING to any Armada record, dispatches
    /// nothing, lands nothing, and has no side effect beyond an optional log line. It never persists
    /// the raw request query — only its length and a short hash reach a log. Its content is
    /// already-sanitized AI-Memory and docs text, so bodies pass through. It is bounded by a
    /// per-mission call budget from <c>contextRetrieval</c> settings; when the budget is spent it
    /// returns a clear budget message.
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

        // Process-local per-mission call budget, keyed by mission id, else participant key, else a
        // shared bucket. Reset on an admiral restart, which ends every running captain.
        private static readonly ConcurrentDictionary<string, int> _CallsByBudgetKey =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the context fetch tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="retrieval">The retrieval service over the built context index. When null the
        /// tool is not registered (a minimal harness without a built index).</param>
        /// <param name="database">Database driver, for resolving the calling mission's vessel and persona.</param>
        /// <param name="settings">Armada settings supplying <c>contextRetrieval</c>.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="participantKeyProvider">Returns the calling captain's participant key, or null.
        /// Used to bucket the budget when no mission id is supplied.</param>
        public static void Register(
            RegisterToolDelegate register,
            ContextRetrievalService? retrieval,
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule? logging = null,
            Func<string?>? participantKeyProvider = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (retrieval == null) return; // No built index; nothing to serve.

            Func<string?> participantKey = participantKeyProvider ?? (() => null);

            register(
                FetchContextToolName,
                "Fetch the memory and docs context relevant to a topic or short task, on demand. Your brief already carries the always-on core rules; this tool returns the relevant LEAF chunks for what you name, so you can pull a procedure or a rule you need without carrying all of memory. Give a 'query' (a short task description) and/or a 'topic', and pass your mission id in 'missionId' so the tool scopes the result to your vessel and persona. It is read-only: it returns text you weigh, dispatches nothing, edits no record, and writes no memory. Its content is already sanitized. It is budgeted per mission; when the budget is spent it says so. Safety leaves for your vessel are always included.",
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
                async (args) => await HandleAsync(args, retrieval, database, settings, logging, participantKey).ConfigureAwait(false));
        }

        /// <summary>
        /// Reset the process-local per-mission call budget. For tests only; production resets it on an
        /// admiral restart.
        /// </summary>
        public static void ResetBudgetForTests()
        {
            _CallsByBudgetKey.Clear();
        }

        #endregion

        #region Private-Methods

        private static async Task<object> HandleAsync(
            JsonElement? args,
            ContextRetrievalService retrieval,
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule? logging,
            Func<string?> participantKeyProvider)
        {
            try
            {
                if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object)
                    return Unavailable("invalid", "The call carried no arguments object.");

                JsonElement root = args.Value;
                string? query = ReadOptionalString(root, "query");
                string? topicArg = ReadOptionalString(root, "topic");
                string? missionId = ReadOptionalString(root, "missionId");

                if (String.IsNullOrWhiteSpace(query) && String.IsNullOrWhiteSpace(topicArg))
                    return Unavailable("invalid", "Give a query or a topic to fetch context for.");

                ContextRetrievalSettings toolSettings = settings.ContextRetrieval;
                if (!toolSettings.FetchToolEnabled)
                    return Unavailable("disabled", "The context fetch tool is not enabled.");

                // Resolve the calling mission's vessel and persona, so the leaf set is scoped. An
                // unresolved id simply leaves the scope open; the read is best-effort and never throws
                // into the caller.
                string? vesselName = null;
                string? persona = null;
                if (!String.IsNullOrWhiteSpace(missionId))
                {
                    try
                    {
                        Mission? mission = await database.Missions.ReadAsync(missionId!, CancellationToken.None).ConfigureAwait(false);
                        if (mission != null)
                        {
                            persona = String.IsNullOrWhiteSpace(mission.Persona) ? null : mission.Persona;
                            if (!String.IsNullOrWhiteSpace(mission.VesselId))
                            {
                                Vessel? vessel = await database.Vessels.ReadAsync(mission.VesselId!, CancellationToken.None).ConfigureAwait(false);
                                if (vessel != null && !String.IsNullOrWhiteSpace(vessel.Name)) vesselName = vessel.Name;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logging?.Warn("[McpContextTools] mission resolve failed for " + missionId + ": " + ex.Message);
                    }
                }

                string? participantKey = participantKeyProvider();
                string budgetKey = !String.IsNullOrWhiteSpace(missionId)
                    ? missionId!
                    : (!String.IsNullOrWhiteSpace(participantKey) ? "participant:" + participantKey : "unscoped");

                // The per-mission call budget. This is a served call, so it counts. When exhausted, no
                // leaves are returned and the caller falls back to reading by hand.
                int used = _CallsByBudgetKey.AddOrUpdate(budgetKey, 1, (_, previous) => previous + 1);
                if (used > toolSettings.MaxCallsPerMission)
                    return BudgetExhausted(used - 1, toolSettings.MaxCallsPerMission);

                ContextRetrievalRequest request = new ContextRetrievalRequest
                {
                    Query = query,
                    Topics = ParseTopics(topicArg),
                    RequestingPersona = persona,
                    Vessel = vesselName,
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

                return BuildAnswer(result, used, toolSettings.MaxCallsPerMission);
            }
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

        private static string? ReadOptionalString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement element)) return null;
            if (element.ValueKind == JsonValueKind.String) return element.GetString();
            if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined) return null;
            return element.GetRawText();
        }

        #endregion
    }
}
