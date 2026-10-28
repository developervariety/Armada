namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>Explicit operator-only Research continuation and immutable prior-output tools.</summary>
    public static class McpResearchReplayTools
    {
        /// <summary>Register the full-chain replay tools; ordinary mission restart is unchanged.</summary>
        /// <param name="register">Tool registry.</param>
        /// <param name="database">Durable store.</param>
        /// <param name="admiral">Current runtime owner.</param>
        /// <param name="settings">Current capacity settings.</param>
        /// <param name="git">Source resolver.</param>
        /// <param name="notifier">Shared event and change notifications.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, IAdmiralService admiral,
            ArmadaSettings settings, IGitService git, OperationNotifier? notifier = null)
        {
            ResearchReplayService service = new ResearchReplayService(database, admiral, settings, git, notifier);
            JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            register("armada_replay_research_voyage",
                "Preview or explicitly replay the same six Research duties of an owned failed voyage. Preview first with dryRun=true and exact sourceCommit. Apply requires a unique requestId and the preview expectedFingerprint. Every original attempt is archived atomically before the same stage IDs reset. Ordinary restart and recovery budgets are unchanged. No stage waiver or report acceptance is granted.",
                new { type = "object", properties = new {
                    voyageId = new { type = "string" }, sourceCommit = new { type = "string" },
                    dryRun = new { type = "boolean" }, requestId = new { type = "string", maxLength = 128 },
                    expectedFingerprint = new { type = "string", minLength = 64, maxLength = 64 }
                }, required = new[] { "voyageId", "sourceCommit", "dryRun" } },
                async args =>
                {
                    try
                    {
                        ResearchReplayRequest request = JsonSerializer.Deserialize<ResearchReplayRequest>(args!.Value, options)!;
                        ReplayIdentity identity = JsonSerializer.Deserialize<ReplayIdentity>(args.Value, options)!;
                        string voyageId = identity.VoyageId;
                        return (object)await service.ExecuteAsync(McpCallerContext.Require(), voyageId, request).ConfigureAwait(false);
                    }
                    catch (UnauthorizedAccessException) { return new { Error = "Current exact-owner global operator authority is required." }; }
                    catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is InvalidOperationException)
                    { return new { Error = "Research replay was refused. Read a fresh full-chain preview; no partial replay was applied." }; }
                });
            register("armada_research_replay_output",
                "Read a redacted digest-backed page of a prior attempt archived by an explicit full Research replay. Global operators and mission credentials in the same current replayed voyage may read it. Historical output never supplies current-stage verdict or completeness. Continue through HasMore=false and preserve the replay identity.",
                new { type = "object", properties = new {
                    replayId = new { type = "string" }, missionId = new { type = "string" },
                    offset = new { type = "integer", minimum = 0 }, length = new { type = "integer", minimum = 1, maximum = MissionOutputArtifact.MaximumPageLength }
                }, required = new[] { "replayId", "missionId" } },
                async args =>
                {
                    try
                    {
                        OutputArguments body = JsonSerializer.Deserialize<OutputArguments>(args!.Value, options)!;
                        MissionOutputArtifactPage? page = await service.ReadOutputAsync(McpCallerContext.Require(),
                            body.ReplayId, body.MissionId, body.Offset, body.Length).ConfigureAwait(false);
                        return page == null ? (object)new { Error = "Prior attempt not found." } : page;
                    }
                    catch (UnauthorizedAccessException) { return new { Error = "Current exact-owner global operator authority is required." }; }
                    catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is InvalidOperationException)
                    { return new { Error = "The immutable attempt output page could not be read." }; }
                });
        }
        private sealed class ReplayIdentity
        {
            public string VoyageId { get; set; } = String.Empty;
        }

        private sealed class OutputArguments
        {
            public string ReplayId { get; set; } = String.Empty;
            public string MissionId { get; set; } = String.Empty;
            public int Offset { get; set; }
            public int Length { get; set; } = MissionOutputArtifact.DefaultPageLength;
        }
    }
}
