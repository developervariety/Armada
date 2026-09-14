namespace Armada.Server
{
    using System.Net.Http;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Evaluates captain access to the tool sources visible from a captain runtime.
    /// </summary>
    public class CaptainToolService
    {
        private readonly DatabaseDriver _database;
        private readonly CaptainRuntimeToolCatalogService _runtimeCatalog;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Armada settings used to locate per-launch runtime configuration.</param>
        /// <param name="httpClient">Optional HTTP client for runtime MCP probes.</param>
        /// <param name="userProfileDirectory">Directory holding the user-level runtime configuration. Defaults to
        /// the current user's profile. The MCP servers configured there may be started to probe them.</param>
        public CaptainToolService(LoggingModule logging, DatabaseDriver database, ArmadaSettings? settings = null, HttpClient? httpClient = null, string? userProfileDirectory = null)
        {
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            if (database == null) throw new ArgumentNullException(nameof(database));

            _database = database;
            _runtimeCatalog = new CaptainRuntimeToolCatalogService(logging, settings, httpClient, userProfileDirectory);
        }

        /// <summary>
        /// Describe the runtime-visible tool sources and named tools available to a captain.
        /// </summary>
        /// <param name="captain">Captain to inspect.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Availability summary and tool list.</returns>
        public async Task<CaptainToolAccessResult> DescribeAsync(Captain captain, CancellationToken token = default, bool plannedAsk = false)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            CaptainToolAccessResult result = new CaptainToolAccessResult
            {
                CaptainId = captain.Id,
                CaptainName = captain.Name,
                Runtime = captain.Runtime.ToString(),
                ArmadaToolCount = 0,
                Tools = new List<CaptainToolSummary>()
            };

            CaptainRuntimeToolCatalogService.RuntimeToolCatalogSnapshot? runtimeSnapshot =
                await _runtimeCatalog.TryDescribeAsync(captain, _database, token, plannedAsk).ConfigureAwait(false);

            if (runtimeSnapshot != null)
            {
                result.ToolsAccessible = runtimeSnapshot.ToolsAccessible;
                result.AvailabilityVerified = runtimeSnapshot.AvailabilityVerified;
                result.McpConnectionPlanned = runtimeSnapshot.McpConnectionPlanned;
                result.AvailabilitySource = runtimeSnapshot.AvailabilitySource;
                result.Summary = runtimeSnapshot.Summary;
                result.ArmadaToolCount = runtimeSnapshot.ArmadaToolCount;
                result.ConfiguredServerCount = runtimeSnapshot.ConfiguredServerCount;
                result.ReachableServerCount = runtimeSnapshot.ReachableServerCount;
                result.EffectiveToolCount = runtimeSnapshot.EffectiveToolCount;
                result.Servers = runtimeSnapshot.Servers;
                result.Tools = runtimeSnapshot.Tools;
                return result;
            }

            result.ToolsAccessible = false;
            result.AvailabilityVerified = false;
            result.AvailabilitySource = "unsupported-runtime";
            result.Summary = "Armada does not currently have a runtime-specific tool inventory implementation for this captain.";
            return result;
        }
    }
}
