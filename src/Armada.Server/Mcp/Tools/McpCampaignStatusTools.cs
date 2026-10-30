namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Core.Models;

    /// <summary>
    /// Registers the campaign status MCP tool: one call that rolls up the objective tree under a
    /// campaign root.
    /// </summary>
    public static class McpCampaignStatusTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers the campaign status tool with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for objective data access.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
        {
            register(
                "armada_campaign_status",
                "One-call status for a campaign: the objective tree under a root (or every root carrying a tag), grouped by parent with statuses, plus the campaign closeout summary. Use this to start a session or answer 'where does this campaign stand' without a pile of enumerations.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        tag = new { type = "string", description = "Campaign tag, for example 'campaign:porting'. Resolves every objective carrying it as a root. Omit when rootObjectiveId is supplied." },
                        rootObjectiveId = new { type = "string", description = "Campaign root objective ID (obj_ prefix). Omit when tag is supplied." },
                        includeFullContent = new { type = "boolean", description = "Return long free-text fields whole instead of previewing them (default false). Previewed fields carry a companion <name>Length, and the response carries TruncatedFieldCount." },
                        includeSlices = new { type = "boolean", description = "Include slice objectives and the full campaign closeout items/evidence review (default false). Summary closeout counts cover every descendant; its first ten remaining items are previewed by default." }
                    }
                },
                async (args) =>
                {
                    CampaignStatusArgs request = args != null && args.HasValue
                        ? JsonSerializer.Deserialize<CampaignStatusArgs>(args.Value, _JsonOptions) ?? new CampaignStatusArgs()
                        : new CampaignStatusArgs();

                    // Resolve roots, then two levels: roots -> lanes/programs -> slices.
                    List<Objective> allObjectives = await database.Objectives.EnumerateAsync().ConfigureAwait(false);
                    List<string> rootIds = new List<string>();
                    if (!String.IsNullOrWhiteSpace(request.RootObjectiveId))
                    {
                        rootIds.Add(request.RootObjectiveId!);
                    }
                    else if (!String.IsNullOrWhiteSpace(request.Tag))
                    {
                        foreach (Objective o in allObjectives)
                        {
                            if (o.Tags != null && o.Tags.Contains(request.Tag!, StringComparer.Ordinal)) rootIds.Add(o.Id);
                        }
                    }

                    if (rootIds.Count == 0)
                        return (object)new { Error = "no campaign roots resolved - supply tag or rootObjectiveId" };

                    HashSet<string> rootSet = new HashSet<string>(rootIds);
                    Dictionary<string, List<CampaignNode>> byParent = new Dictionary<string, List<CampaignNode>>();

                    void AddToParent(string parentId, Objective o)
                    {
                        if (!byParent.TryGetValue(parentId, out List<CampaignNode> list))
                        {
                            list = new List<CampaignNode>();
                            byParent[parentId] = list;
                        }

                        list.Add(CampaignNode.From(o));
                    }

                    var groupIds = new HashSet<string>();
                    foreach (Objective o in allObjectives)
                    {
                        if (String.IsNullOrEmpty(o.ParentObjectiveId)) continue;
                        if (!rootSet.Contains(o.ParentObjectiveId!)) continue;
                        AddToParent(o.ParentObjectiveId!, o);
                        groupIds.Add(o.Id);
                    }

                    // Second level: children of the level-one groups (lanes -> slices).
                    foreach (Objective o in allObjectives)
                    {
                        if (String.IsNullOrEmpty(o.ParentObjectiveId)) continue;
                        if (!groupIds.Contains(o.ParentObjectiveId!)) continue;
                        AddToParent(o.ParentObjectiveId!, o);
                    }

                    bool includeSlices = args.HasValue
                        && args.Value.TryGetProperty("includeSlices", out JsonElement _slices)
                        && _slices.ValueKind == JsonValueKind.True;

                    var tree = new List<object>();
                    foreach (string rootId in rootIds)
                    {
                        Objective? root = allObjectives.FirstOrDefault(o => o.Id == rootId);
                        var lanes = new List<object>();
                        if (byParent.TryGetValue(rootId, out List<CampaignNode>? laneNodes))
                        {
                            foreach (CampaignNode lane in laneNodes)
                            {
                                byParent.TryGetValue(lane.Id, out List<CampaignNode>? slices);
                                List<CampaignNode> laneSlices = slices ?? new List<CampaignNode>();

                                // A campaign rollup is about lanes; the slices under them are
                                // most of the payload. One live campaign held 161 nodes at
                                // 102,096 characters, past the caller's limit, while the
                                // lane-level view is what the tool is actually for. Slices are
                                // therefore counted by default and returned on request.
                                if (includeSlices)
                                    lanes.Add(new { objective = lane, children = laneSlices, sliceCount = laneSlices.Count });
                                else
                                    lanes.Add(new { objective = lane, sliceCount = laneSlices.Count });
                            }
                        }

                        // Project the root the same way as its lanes. Serializing the full
                        // Objective here was most of the payload: 24 roots at 1.4 to 4.8 KB
                        // each, roughly 70 KB, against lanes of a few hundred bytes. A
                        // rollup wants the same summary at every level.
                        tree.Add(new
                        {
                            objective = root == null ? null : CampaignNode.From(root),
                            children = lanes
                        });
                    }

                    bool wantsFull = args.HasValue
                        && args.Value.TryGetProperty("includeFullContent", out JsonElement _full)
                        && _full.ValueKind == JsonValueKind.True;
                    List<CampaignCloseoutReport> closeouts = new List<CampaignCloseoutReport>();
                    foreach (string rootId in rootIds)
                        closeouts.Add(await CampaignCloseoutEvaluator.EvaluateAsync(database, rootId, allObjectives).ConfigureAwait(false));
                    return McpResultPreview.Apply(
                        new
                        {
                            Roots = rootIds, Tree = tree,
                            Closeout = closeouts.Select(closeout =>
                            {
                                return new
                                {
                                    closeout.RootObjectiveId, ObjectiveCount = closeout.Objectives.Count,
                                    closeout.ReadyForEvidenceReview, RemainingItemCount = closeout.Items.Count,
                                    Items = includeSlices ? closeout.Items : closeout.Items.Take(10).ToList(),
                                    ItemsTruncated = !includeSlices && closeout.Items.Count > 10,
                                    EvidenceReview = includeSlices ? closeout.EvidenceReview : null,
                                    RecordedDeliveredWorkCount = closeout.RecordedDeliveredWork.Count,
                                    RecordedDeliveredWork = includeSlices ? closeout.RecordedDeliveredWork : closeout.RecordedDeliveredWork.Take(10).ToList(),
                                    RecordedDeliveredWorkTruncated = !includeSlices && closeout.RecordedDeliveredWork.Count > 10,
                                    closeout.EvidenceReviewRequirement
                                };
                            }).ToList()
                        },
                        wantsFull);
                });
        }

        /// <summary>
        /// Serializable rollup row for one campaign-tree objective.
        /// </summary>
        public sealed class CampaignNode
        {
            /// <summary>Objective identifier.</summary>
            public string Id { get; set; } = String.Empty;

            /// <summary>Title.</summary>
            public string Title { get; set; } = String.Empty;

            /// <summary>Lifecycle status.</summary>
            public ObjectiveStatusEnum? Status { get; set; }

            /// <summary>Backlog state.</summary>
            public ObjectiveBacklogStateEnum? BacklogState { get; set; }

            /// <summary>Kind.</summary>
            public ObjectiveKindEnum? Kind { get; set; }

            /// <summary>Priority.</summary>
            public ObjectivePriorityEnum? Priority { get; set; }

            /// <summary>Parent identifier.</summary>
            public string? ParentObjectiveId { get; set; }

            /// <summary>Tags.</summary>
            public List<string>? Tags { get; set; }

            /// <summary>
            /// Build a rollup row from an objective.
            /// </summary>
            public static CampaignNode From(Objective objective)
            {
                return new CampaignNode
                {
                    Id = objective.Id,
                    Title = objective.Title,
                    Status = objective.Status,
                    BacklogState = objective.BacklogState,
                    Kind = objective.Kind,
                    Priority = objective.Priority,
                    ParentObjectiveId = objective.ParentObjectiveId,
                    Tags = objective.Tags
                };
            }
        }

        private sealed class CampaignStatusArgs
        {
            public string? Tag { get; set; } = null;
            public string? RootObjectiveId { get; set; } = null;
        }
    }
}
