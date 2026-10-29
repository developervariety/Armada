namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers <c>armada_mission_attempts</c>, the MCP surface of <see cref="MissionAttemptProvenanceReader"/>.
    /// The REST route <c>GET /api/v1/missions/{id}/attempts</c> calls the same reader, so both surfaces share one
    /// authorization rule and one answer shape.
    /// </summary>
    public static class McpMissionAttemptTools
    {
        #region Public-Members

        /// <summary>Registered name of the mission attempts tool.</summary>
        public const string ToolName = "armada_mission_attempts";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the mission attempts tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));

            register(
                ToolName,
                "List the recorded attempts of one mission, oldest first. Each attempt names the captain, runtime, model and runtime version that ran it, when it started and ended, and how it ended (Running, Completed, Failed or Interrupted, with a reason code for the last two). The runtime version is the executable's own --version line and is empty when the probe was unavailable or the attempt ran on a Harbor runner. Reads only missions in the caller's scope; an unreadable mission answers not found. Read-only and bounded: 'Truncated' true means older attempts exist beyond 'limit'.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission id (msn_ prefix)." },
                        limit = new { type = "integer", description = "Most attempts to return, newest kept (default 100, maximum 1000)." }
                    },
                    required = new[] { "missionId" }
                },
                (args) => HandleAsync(args, database));
        }

        #endregion

        #region Private-Methods

        private static async Task<object> HandleAsync(JsonElement? args, DatabaseDriver database)
        {
            AttemptArgs request = new AttemptArgs();
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object)
                request = JsonSerializer.Deserialize<AttemptArgs>(args.Value, _JsonOptions) ?? new AttemptArgs();

            AuthContext caller = McpCallerContext.Require();
            CancellationToken callCancellation = McpCallerContext.CallToken;
            MissionAttemptProvenanceReport? report = await MissionAttemptProvenanceReader.ReadAsync(
                database, caller, request.MissionId, request.Limit ?? MissionAttemptProvenanceReader.DefaultLimit, callCancellation).ConfigureAwait(false);
            if (report == null) return new { Error = "Mission not found" };
            return report;
        }

        #endregion

        #region Private-Classes

        private sealed class AttemptArgs
        {
            public string? MissionId { get; set; } = null;

            public int? Limit { get; set; } = null;
        }

        #endregion
    }
}
