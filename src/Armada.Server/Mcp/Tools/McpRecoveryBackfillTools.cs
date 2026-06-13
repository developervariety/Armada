namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// MCP tool for backfill detection of false-positive auto-rescue landings.
    /// armada_detect_false_rescue_landings: returns rescue missions flagged as likely false positives.
    /// </summary>
    public static class McpRecoveryBackfillTools
    {
        #region Public-Methods

        /// <summary>
        /// Registers the recovery backfill MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="logging">Logging module.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            LoggingModule logging)
        {
            register(
                "armada_detect_false_rescue_landings",
                "Scans Complete auto-rescue missions for false-positive landing indicators (reviewer persona, empty commit hash, noop merge, identity commit). Read-only -- no mutations.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Optional: restrict detection to one vessel" }
                    }
                },
                async (args) =>
                {
                    DetectFalseRescueLandingsArgs parsedArgs = new DetectFalseRescueLandingsArgs();
                    if (args.HasValue)
                    {
                        DetectFalseRescueLandingsArgs? deserialized = JsonSerializer.Deserialize<DetectFalseRescueLandingsArgs>(
                            args.Value.GetRawText());
                        if (deserialized != null)
                            parsedArgs = deserialized;
                    }

                    RescueLandingBackfillDetector detector = new RescueLandingBackfillDetector(database, logging);
                    List<SuspectRescueLanding> suspects = await detector
                        .DetectAsync(parsedArgs.VesselId).ConfigureAwait(false);

                    return new
                    {
                        count = suspects.Count,
                        suspects
                    };
                });
        }

        #endregion

        #region Private-Members

        private class DetectFalseRescueLandingsArgs
        {
            /// <summary>
            /// Optional vessel identifier to restrict detection to one vessel.
            /// </summary>
            public string? VesselId { get; set; } = null;
        }

        #endregion
    }
}
