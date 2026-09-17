namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Registers the operator-facing change_quality gate tool. It reviews a focused diff across the
    /// quality dimensions and routes the routable (deterministically-backed) weaknesses to a single
    /// Triaged objective (auto-dispatch OFF). Operator-scoped: it is not in the caller-scoped catalogue,
    /// so a mission captain cannot reach it; the captain has the read-only <c>armada_change_quality</c>.
    /// </summary>
    public static class McpChangeQualityTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Registered name of the operator change-quality gate tool.</summary>
        public const string GateToolName = "armada_change_quality_gate";

        /// <summary>
        /// Register the change_quality gate tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="adapter">The change_quality adapter, or null to run the deterministic rule only.</param>
        /// <param name="router">The follow-up router that files the Triaged row, or null to file nothing.</param>
        /// <param name="logging">Optional logging module.</param>
        public static void Register(
            RegisterToolDelegate register,
            TypedChangeQualityAdapter? adapter,
            IFollowUpRouter? router,
            LoggingModule? logging)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));

            register(
                GateToolName,
                "Review a focused diff for change quality and route its routable weaknesses to a Triaged objective. Give the unified diff in 'diff' and the vessel in 'vesselId'. The deterministic backing (the Slop core-rule check and the complexity metric) is authoritative and hard-flags; the change_quality model adds informational weaknesses on top. When the change carries a routable (deterministically-backed) weakness, exactly one Triaged objective is filed (auto-dispatch OFF) through the follow-up router; nothing is ever dispatched. Returns the per-dimension weaknesses and the filed objective id, if any. Operator tool.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix) the change belongs to." },
                        diff = new { type = "string", description = "The unified diff to review." },
                        missionId = new { type = "string", description = "Optional reviewed mission id, recorded on the filed follow-up." },
                        centralPackageManagement = new { type = "boolean", description = "Whether Central Package Management is enabled, for the Slop project rules (default false)." }
                    },
                    required = new[] { "vesselId", "diff" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    GateArgs request = JsonSerializer.Deserialize<GateArgs>(args.Value, _JsonOptions) ?? new GateArgs();
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Diff)) return (object)new { Error = "diff is required" };

                    ChangeQualityGateResult result = await ChangeQualityGate.ReviewAndRouteAsync(
                        request.Diff, request.VesselId, request.MissionId, request.CentralPackageManagement,
                        adapter, router, logging, CancellationToken.None).ConfigureAwait(false);

                    return (object)new
                    {
                        VesselId = request.VesselId,
                        Weaknesses = result.Verdict.Weaknesses.Select(w => new
                        {
                            w.Dimension,
                            Severity = w.Severity.ToString(),
                            Source = w.Source.ToString(),
                            w.Reason
                        }).ToList(),
                        RoutableCount = result.Verdict.RoutableWeaknesses.Count,
                        FiledObjectiveId = result.CreatedObjectiveId
                    };
                });
        }

        private sealed class GateArgs
        {
            public string VesselId { get; set; } = "";

            public string Diff { get; set; } = "";

            public string? MissionId { get; set; } = null;

            public bool CentralPackageManagement { get; set; } = false;
        }
    }
}
