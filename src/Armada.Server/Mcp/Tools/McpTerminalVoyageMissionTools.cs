namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the operator tool that reconciles WorkProduced missions under ended voyages.
    /// </summary>
    public static class McpTerminalVoyageMissionTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Register the terminal-voyage mission reconciliation tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="reconciler">Reconciler.</param>
        /// <param name="jobs">Optional shared job service; when present the pass runs in the background.</param>
        public static void Register(RegisterToolDelegate register, TerminalVoyageMissionReconciler reconciler, LongRunningJobService? jobs)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (reconciler == null) throw new ArgumentNullException(nameof(reconciler));

            register(
                "armada_reconcile_terminal_voyage_missions",
                "Reconcile missions still in WorkProduced after their voyage ended. Landed work (commit on the vessel default branch, or a Landed merge entry) becomes Complete; unlanded work becomes Failed under a Failed voyage and Cancelled otherwise, with a named reason. Missions whose ancestry is unknown, whose landing is still in flight, whose voyage ended inside the grace period, or whose Complete voyage requested no landing are kept and counted by reason. Never deletes branches, refs or commits. dryRun defaults to true and writes nothing."
                    + (jobs != null ? " Runs in the background and returns a job handle; use armada_job_status for the result." : String.Empty),
                new
                {
                    type = "object",
                    properties = new
                    {
                        dryRun = new { type = "boolean", description = "Report what would change without writing (default true)" },
                        includeHistorical = new { type = "boolean", description = "Include voyages that ended more than 24 hours ago (default true for this operator tool)" },
                        voyageId = new { type = "string", description = "Restrict to one voyage (vyg_ prefix)" },
                        vesselId = new { type = "string", description = "Restrict to one vessel (vsl_ prefix)" },
                        maxItems = new { type = "integer", description = "Maximum per-mission items in the result, 0-5000 (default 200); counts always cover every mission" }
                    }
                },
                async (args) =>
                {
                    ReconcileArgs parsed = args.HasValue
                        ? JsonSerializer.Deserialize<ReconcileArgs>(args.Value, _JsonOptions) ?? new ReconcileArgs()
                        : new ReconcileArgs();

                    TerminalVoyageMissionReconciliationRequest request = new TerminalVoyageMissionReconciliationRequest
                    {
                        DryRun = parsed.DryRun ?? true,
                        IncludeHistorical = parsed.IncludeHistorical ?? true,
                        VoyageId = parsed.VoyageId,
                        VesselId = parsed.VesselId,
                        MaxItems = parsed.MaxItems ?? 200
                    };

                    if (jobs != null)
                    {
                        string kind = request.DryRun ? "terminal_voyage_missions_dry_run" : "terminal_voyage_missions_apply";
                        return (object)jobs.Start(kind, async (CancellationToken token) =>
                            (object?)await reconciler.ReconcileAsync(request, token).ConfigureAwait(false));
                    }

                    return (object)await reconciler.ReconcileAsync(request, CancellationToken.None).ConfigureAwait(false);
                });
        }

        private sealed class ReconcileArgs
        {
            public bool? DryRun { get; set; } = null;

            public bool? IncludeHistorical { get; set; } = null;

            public string? VoyageId { get; set; } = null;

            public string? VesselId { get; set; } = null;

            public int? MaxItems { get; set; } = null;
        }
    }
}
