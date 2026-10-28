namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Builds the one-line description of a saved Ask action proposal. A vessel the arguments name is shown by its
    /// name when the proposal owner's tenant holds it, and by its id otherwise. The saved arguments are never changed.
    /// </summary>
    public static class AskProposalSummary
    {
        private const int _MaxVessels = 20;
        private const int _MaxLength = 300;

        /// <summary>Set <see cref="AskActionProposal.Summary"/> from the proposal's tool and arguments.</summary>
        /// <param name="database">Database driver used to look up vessel names.</param>
        /// <param name="proposal">Proposal to describe.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task ApplyAsync(DatabaseDriver database, AskActionProposal proposal, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            JsonObject? args = Parse(proposal.ArgumentsJson);
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (args != null && !String.IsNullOrWhiteSpace(proposal.TenantId))
            {
                foreach (string id in VesselIds(args))
                {
                    Vessel? vessel = await database.Vessels.ReadAsync(proposal.TenantId, id, token).ConfigureAwait(false);
                    if (vessel != null && !String.IsNullOrWhiteSpace(vessel.Name)) names[id] = vessel.Name;
                }
            }
            proposal.Summary = Describe(proposal.ToolName, args, names);
        }

        /// <summary>Describe a tool call, showing each vessel id as the supplied name when one is known.</summary>
        /// <param name="toolName">Tool name.</param>
        /// <param name="args">Parsed arguments, or null when the saved arguments are not a JSON object.</param>
        /// <param name="vesselNames">Vessel id to name lookup.</param>
        /// <returns>The description.</returns>
        public static string Describe(string toolName, JsonObject? args, IReadOnlyDictionary<string, string> vesselNames)
        {
            string Text(string key)
            {
                JsonNode? node = args?[key];
                return node is JsonValue value ? value.ToString() : String.Empty;
            }
            string Vessel(string id) => vesselNames.TryGetValue(id, out string? name) ? name : id;

            string tool = toolName ?? String.Empty;
            string vesselId = Text("vesselId");
            string onVessel = vesselId.Length > 0 ? " on vessel " + Vessel(vesselId) : String.Empty;
            string text;
            switch (tool)
            {
                case "armada_dispatch":
                    text = "Dispatch voyage \"" + Text("title") + "\"" + (vesselId.Length > 0 ? " to vessel " + Vessel(vesselId) : String.Empty);
                    break;
                case "armada_create_mission":
                    text = "Create mission \"" + Text("title") + "\"" + onVessel;
                    break;
                case "armada_update_mission":
                    text = "Update mission " + Text("missionId") + onVessel;
                    break;
                case "armada_cancel_mission":
                    text = "Cancel mission " + Text("missionId");
                    break;
                case "armada_restart_mission":
                    text = "Restart mission " + Text("missionId");
                    break;
                case "armada_retry_landing":
                    text = "Retry landing for mission " + Text("missionId");
                    break;
                case "armada_cancel_voyage":
                    text = "Cancel voyage " + Text("voyageId");
                    break;
                default:
                    text = "Run " + tool + VesselSuffix(args, vesselNames);
                    break;
            }
            return text.Length > _MaxLength ? text.Substring(0, _MaxLength) + "..." : text;
        }

        private static string VesselSuffix(JsonObject? args, IReadOnlyDictionary<string, string> vesselNames)
        {
            if (args == null) return String.Empty;
            List<string> labels = new List<string>();
            foreach (string id in VesselIds(args)) labels.Add(vesselNames.TryGetValue(id, out string? name) ? name : id);
            return labels.Count == 0 ? String.Empty : " on vessel " + String.Join(", ", labels);
        }

        /// <summary>The vessel ids a proposal's saved arguments name, without any lookup.</summary>
        /// <param name="argumentsJson">Saved canonical arguments.</param>
        /// <returns>The vessel ids; empty when the arguments name none or are not a JSON object.</returns>
        public static List<string> VesselIdsOf(string? argumentsJson)
        {
            JsonObject? args = Parse(argumentsJson);
            return args == null ? new List<string>() : VesselIds(args);
        }

        private static List<string> VesselIds(JsonObject args)
        {
            List<string> ids = new List<string>();
            if (args["vesselId"] is JsonValue single && single.TryGetValue(out string? one) && !String.IsNullOrWhiteSpace(one)) ids.Add(one);
            if (args["vesselIds"] is JsonArray many)
            {
                foreach (JsonNode? item in many)
                {
                    if (ids.Count >= _MaxVessels) break;
                    if (item is JsonValue value && value.TryGetValue(out string? id) && !String.IsNullOrWhiteSpace(id) && !ids.Contains(id)) ids.Add(id);
                }
            }
            return ids;
        }

        // Saved arguments are canonical JSON objects; anything else is described by the tool name alone.
        private static JsonObject? Parse(string? argumentsJson)
        {
            if (String.IsNullOrWhiteSpace(argumentsJson)) return null;
            try { return JsonNode.Parse(argumentsJson) as JsonObject; }
            catch (JsonException) { return null; }
        }
    }
}
