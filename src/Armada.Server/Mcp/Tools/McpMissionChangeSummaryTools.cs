namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Registers <c>armada_mission_change_summary</c>, the MCP surface of <see cref="MissionChangeSummaryReader"/>.
    /// The REST route <c>GET /api/v1/missions/{id}/change-summary</c> calls the same reader, so both surfaces share
    /// one authorization rule, one set of bounds and one answer shape.
    /// </summary>
    public static class McpMissionChangeSummaryTools
    {
        #region Public-Members

        /// <summary>Registered name of the mission change summary tool.</summary>
        public const string ToolName = "armada_mission_change_summary";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the mission change summary tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Settings supplying the log directory.</param>
        /// <param name="git">Git service; null answers <c>git_unavailable</c>.</param>
        /// <param name="logging">Optional logging module.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService? git,
            LoggingModule? logging = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            register(
                "armada_mission_change_summary",
                "List every path one mission stage committed, with its status (Added, Modified, Deleted, Renamed, TypeChanged), comparing the commit the stage started from with its recorded final commit. It reads the vessel's repository and needs no dock, so it still answers after the dock is reclaimed. It is not a diff against the current target branch and holds no uncommitted files. A mission credential always reads its own mission, whatever id is passed. The answer states both full commit ids; moving a branch never changes them. When it cannot compare, 'Available' is false and 'Reason' names why (for example start_commit_not_recorded, final_commit_missing, timeout, output_limit_exceeded): that never means nothing changed. 'Complete' false with Reason path_limit means only the first 'maxPaths' paths are listed and 'TotalPathCount' gives the total. Read-only, bounded, and budgeted per mission credential.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission id (msn_ prefix). A mission credential may omit it; its own mission is always used." },
                        maxPaths = new { type = "integer", description = "Most paths to list (default 1000, maximum 5000)." }
                    }
                },
                (args) => HandleAsync(args, database, settings, git, logging));
        }

        #endregion

        #region Private-Methods

        private static async Task<object> HandleAsync(
            JsonElement? args,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService? git,
            LoggingModule? logging)
        {
            ChangeSummaryArgs request = new ChangeSummaryArgs();
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object)
                request = JsonSerializer.Deserialize<ChangeSummaryArgs>(args.Value, _JsonOptions) ?? new ChangeSummaryArgs();

            AuthContext caller = McpCallerContext.Require();
            CancellationToken callCancellation = McpCallerContext.CallToken;
            MissionChangeSummary summary = await MissionChangeSummaryReader.ReadAsync(
                database, settings.LogDirectory, git, caller, request.MissionId, request.MaxPaths,
                null, logging, callCancellation).ConfigureAwait(false);
            return summary;
        }

        #endregion

        #region Private-Classes

        private sealed class ChangeSummaryArgs
        {
            public string? MissionId { get; set; } = null;

            public int? MaxPaths { get; set; } = null;
        }

        #endregion
    }
}
