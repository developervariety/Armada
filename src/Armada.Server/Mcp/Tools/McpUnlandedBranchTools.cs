namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the MCP tool that reports Armada mission branches which were never landed.
    /// </summary>
    public static class McpUnlandedBranchTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers the unlanded-branch reporting tool.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="unlandedBranches">Unlanded-branch reporting service.</param>
        public static void Register(RegisterToolDelegate register, UnlandedBranchService unlandedBranches)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (unlandedBranches == null) throw new ArgumentNullException(nameof(unlandedBranches));

            register(
                "armada_unlanded_branches",
                "Report Armada mission branches that exist in a vessel's repository but were never merged into its default branch. Run without arguments for a fleet-wide count, or pass vesselId to inspect one vessel. Use this to keep stranded mission work visible instead of discovering it by accident.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Optional vessel ID (vsl_ prefix); omit to report every vessel" },
                        includeBranches = new { type = "boolean", description = "Include the individual branch entries, not just counts (default false)" }
                    }
                },
                async (args) =>
                {
                    UnlandedBranchArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<UnlandedBranchArgs>(args.Value, _JsonOptions)!
                        : new UnlandedBranchArgs();

                    List<UnlandedBranchReport> reports;
                    try
                    {
                        reports = await unlandedBranches.BuildReportAsync(request.VesselId).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return (object)new { Error = ex.Message, Code = "vessel_not_found", VesselId = request.VesselId };
                    }

                    int totalUnlanded = reports.Sum(r => r.UnlandedCount);
                    List<string> unmeasured = reports.Where(r => r.Error != null).Select(r => r.VesselName).ToList();

                    // Counts by default: a fleet-wide report with every branch inlined is large and
                    // this tool is meant to be cheap enough to run on a schedule.
                    if (!request.IncludeBranches)
                    {
                        return (object)new
                        {
                            TotalUnlanded = totalUnlanded,
                            VesselsWithUnlanded = reports.Count(r => r.UnlandedCount > 0),
                            UnmeasuredVessels = unmeasured,
                            Vessels = reports
                                .Where(r => r.UnlandedCount > 0 || r.Error != null)
                                .Select(r => new
                                {
                                    r.VesselId,
                                    r.VesselName,
                                    r.DefaultBranch,
                                    r.MissionBranchCount,
                                    r.UnlandedCount,
                                    r.Error
                                })
                                .ToList()
                        };
                    }

                    return (object)new
                    {
                        TotalUnlanded = totalUnlanded,
                        VesselsWithUnlanded = reports.Count(r => r.UnlandedCount > 0),
                        UnmeasuredVessels = unmeasured,
                        Vessels = reports
                    };
                });
        }
    }
}
