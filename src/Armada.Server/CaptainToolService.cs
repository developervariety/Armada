namespace Armada.Server
{
    using System.Net.Http;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes.Mcp;
    using SyslogLogging;

    /// <summary>
    /// Evaluates captain access to the tool sources visible from a captain runtime.
    /// </summary>
    public class CaptainToolService
    {
        private readonly DatabaseDriver _database;
        private readonly CaptainRuntimeToolCatalogService _runtimeCatalog;
        private readonly LoggingModule _logging;
        private readonly ArmadaSettings? _settings;
        private readonly ISessionTokenService? _sessionTokens;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Armada settings used to locate per-launch runtime configuration.</param>
        /// <param name="httpClient">Optional HTTP client for runtime MCP probes.</param>
        /// <param name="userProfileDirectory">Directory holding the user-level runtime configuration. Defaults to
        /// the current user's profile. The MCP servers configured there may be started to probe them.</param>
        /// <param name="sessionTokens">Session token service. It issues a requesting caller's MCP access for an
        /// API-endpoint captain (the same way Ask chat does) and mints the mission owner's own scoped token for a
        /// running mission captain's Armada MCP probe. Null reports no Armada MCP tools for those captains.</param>
        public CaptainToolService(LoggingModule logging, DatabaseDriver database, ArmadaSettings? settings = null, HttpClient? httpClient = null, string? userProfileDirectory = null, ISessionTokenService? sessionTokens = null)
        {
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            if (database == null) throw new ArgumentNullException(nameof(database));

            _database = database;
            _logging = logging;
            _settings = settings;
            _sessionTokens = sessionTokens;
            _runtimeCatalog = new CaptainRuntimeToolCatalogService(logging, settings, httpClient, userProfileDirectory, sessionTokens);
        }

        /// <summary>
        /// Describe the runtime-visible tool sources and named tools available to a captain.
        /// </summary>
        /// <param name="captain">Captain to inspect.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="plannedAsk">True to describe the next Ask launch instead of the active captain launch.</param>
        /// <param name="caller">The requesting caller. For an API-endpoint captain the report lists the Armada MCP
        /// tools this caller would be offered in Ask chat; a null or unauthenticated caller is offered none.</param>
        /// <returns>Availability summary and tool list.</returns>
        public async Task<CaptainToolAccessResult> DescribeAsync(Captain captain, CancellationToken token = default, bool plannedAsk = false, AuthContext? caller = null)
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

            // An API-endpoint captain has no MCP client of its own: an Ask chat turn connects with the caller's
            // access. The report issues that same access, so what it lists is what a chat turn would receive.
            CallerMcpToolAccess? callerAccess = captain.Runtime == AgentRuntimeEnum.ApiEndpoint
                ? CallerMcpToolAccessFactory.Create(caller, _sessionTokens, _settings == null ? 0 : _settings.McpPort, _logging)
                : null;

            CaptainRuntimeToolCatalogService.RuntimeToolCatalogSnapshot? runtimeSnapshot =
                await _runtimeCatalog.TryDescribeAsync(captain, _database, token, plannedAsk, callerAccess).ConfigureAwait(false);

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
