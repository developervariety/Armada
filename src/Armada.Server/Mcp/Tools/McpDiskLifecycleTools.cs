namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the MCP tool that exposes disk-lifecycle observability and bounded
    /// reclamation for Armada-owned storage.
    /// </summary>
    public static class McpDiskLifecycleTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Register the disk-lifecycle MCP tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="diskLifecycle">Disk-lifecycle service.</param>
        public static void Register(RegisterToolDelegate register, DiskLifecycleService diskLifecycle)
        {
            RegisterInternal(register, diskLifecycle, null);
        }

        /// <summary>
        /// Register the disk-lifecycle MCP tool with a shared background job service.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="diskLifecycle">Disk-lifecycle service.</param>
        /// <param name="jobs">Shared process-local job service.</param>
        public static void Register(RegisterToolDelegate register, DiskLifecycleService diskLifecycle, LongRunningJobService jobs)
        {
            if (jobs == null) throw new ArgumentNullException(nameof(jobs));
            RegisterInternal(register, diskLifecycle, jobs);
        }

        private static void RegisterInternal(
            RegisterToolDelegate register,
            DiskLifecycleService diskLifecycle,
            LongRunningJobService? jobs)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (diskLifecycle == null) throw new ArgumentNullException(nameof(diskLifecycle));

            register(
                "armada_disk_lifecycle",
                jobs == null
                    ? "Scan or reconcile Armada-owned disk storage. Action 'scan' reports bytes per owned category and reclaimable items without deleting anything. Action 'reconcile' also purges stale sibling leases and, when diskLifecycle.enabled is true and diskLifecycle.dryRun is false, deletes reclaimable items (orphan docks, expired logs/diffs/instructions, leftover worktrees, temp artifacts, old backups). Reclamation fails closed: only paths under allowed roots, not symlinks, past their grace period, and not referenced by active state are eligible."
                    : "Start a disk-lifecycle scan or reconcile in the background and immediately return an accepted job handle. Use armada_job_status to retrieve completion or failure. Action 'scan' reports bytes per owned category and reclaimable items without deleting anything. Action 'reconcile' also purges stale sibling leases and, when diskLifecycle.enabled is true and diskLifecycle.dryRun is false, deletes reclaimable items. Reclamation fails closed: only paths under allowed roots, not symlinks, past their grace period, and not referenced by active state are eligible.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", description = "'scan' (report only, never deletes) or 'reconcile' (report plus safe reclamation per settings)" },
                        includeActions = new { type = "boolean", description = "Include per-item disposition records in the result (default false)" }
                    },
                    required = new[] { "action" }
                },
                async (args) =>
                {
                    if (!args.HasValue)
                    {
                        return (object)new { Error = "missing args" };
                    }

                    DiskLifecycleArgs request = JsonSerializer.Deserialize<DiskLifecycleArgs>(args.Value, _JsonOptions)!;
                    string action = String.IsNullOrWhiteSpace(request.Action) ? "scan" : request.Action.Trim().ToLowerInvariant();

                    if (!String.Equals(action, "scan", StringComparison.Ordinal)
                        && !String.Equals(action, "reconcile", StringComparison.Ordinal))
                    {
                        return (object)new { Error = "action must be 'scan' or 'reconcile'" };
                    }

                    Func<CancellationToken, Task<Armada.Core.Models.DiskLifecycleReport>> operation = (token) =>
                        String.Equals(action, "reconcile", StringComparison.Ordinal)
                            ? diskLifecycle.ReconcileAsync(token)
                            : diskLifecycle.ScanAsync(token);

                    if (jobs != null)
                    {
                        return (object)jobs.Start(
                            "disk_lifecycle_" + action,
                            async (token) =>
                            {
                                Armada.Core.Models.DiskLifecycleReport report = await operation(token).ConfigureAwait(false);
                                if (request.IncludeActions)
                                {
                                    return (object)report;
                                }
                                return (object)new
                                {
                                    report.ScannedUtc,
                                    report.Enabled,
                                    report.DryRun,
                                    report.TotalBytes,
                                    report.TotalReclaimableBytes,
                                    report.ReclaimableItems,
                                    report.SkippedItems,
                                    report.ProtectedItems,
                                    report.Categories
                                };
                            });
                    }

                    Armada.Core.Models.DiskLifecycleReport direct = await operation(CancellationToken.None).ConfigureAwait(false);
                    if (request.IncludeActions)
                    {
                        return (object)direct;
                    }
                    return (object)new
                    {
                        direct.ScannedUtc,
                        direct.Enabled,
                        direct.DryRun,
                        direct.TotalBytes,
                        direct.TotalReclaimableBytes,
                        direct.ReclaimableItems,
                        direct.SkippedItems,
                        direct.ProtectedItems,
                        direct.Categories
                    };
                });
        }

        /// <summary>
        /// Tool arguments for the disk-lifecycle tool.
        /// </summary>
        private sealed class DiskLifecycleArgs
        {
            public string? Action { get; set; } = null;

            public bool IncludeActions { get; set; } = false;
        }
    }
}
