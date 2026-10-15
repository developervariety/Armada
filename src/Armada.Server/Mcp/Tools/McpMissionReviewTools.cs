namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// MCP tools for pipeline review gates: approve or deny a mission waiting in Review, through the same service the
    /// dashboard and REST use (so the next stage gets the branch, diff, and output). Captain sessions are refused: a
    /// captain can never pass its own gate.
    /// </summary>
    public static class McpMissionReviewTools
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Registers the review gate tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database.</param>
        /// <param name="missions">Mission service, or null where review gates cannot be decided (the tools answer Unavailable).</param>
        /// <exception cref="ArgumentNullException">Thrown when register or database is null.</exception>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, IMissionService? missions)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));

            register(
                "approve_mission_review",
                "Approve a mission waiting at a pipeline review gate (status Review). A non-final stage continues to the next stage with this stage's branch, diff, and output; a final stage continues to landing. Set conditional with a comment to pass guidance to the following stages. Refused for captain sessions.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                        comment = new { type = "string", description = "Optional reviewer comment" },
                        conditional = new { type = "boolean", description = "Approve with conditions: the comment is added as guidance for the following stages" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    MissionReviewArgs request = JsonSerializer.Deserialize<MissionReviewArgs>(args!.Value, _JsonOptions)!;
                    object? refused = await CheckAsync(database, missions, request).ConfigureAwait(false);
                    if (refused != null) return refused;
                    AuthContext caller = McpToolHelpers.ResolveCallerContext();
                    try
                    {
                        return (object)await missions!.ApproveReviewAsync(request.MissionId, caller.UserId, request.Comment, request.Conditional).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return (object)McpToolError.Conflict(ex.Message);
                    }
                });

            register(
                "deny_mission_review",
                "Deny a mission waiting at a pipeline review gate (status Review). RetryStage runs the stage again on the same branch with the comment added to its instructions; FailPipeline fails the mission and cancels the following stages. Without action the stage's configured deny action applies. Refused for captain sessions.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                        comment = new { type = "string", description = "What to change; added to the retried stage's instructions" },
                        action = new { type = "string", description = "RetryStage or FailPipeline (optional; defaults to the stage's configured action)" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    MissionReviewArgs request = JsonSerializer.Deserialize<MissionReviewArgs>(args!.Value, _JsonOptions)!;
                    object? refused = await CheckAsync(database, missions, request).ConfigureAwait(false);
                    if (refused != null) return refused;
                    ReviewDenyActionEnum? action = null;
                    if (!String.IsNullOrWhiteSpace(request.Action))
                    {
                        if (!Enum.TryParse<ReviewDenyActionEnum>(request.Action, true, out ReviewDenyActionEnum parsed))
                            return (object)McpToolError.InvalidArgument("action must be RetryStage or FailPipeline");
                        action = parsed;
                    }

                    AuthContext caller = McpToolHelpers.ResolveCallerContext();
                    try
                    {
                        return (object)await missions!.DenyReviewAsync(request.MissionId, caller.UserId, request.Comment, action).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return (object)McpToolError.Conflict(ex.Message);
                    }
                });
        }

        #endregion

        #region Private-Methods

        private static async Task<object?> CheckAsync(DatabaseDriver database, IMissionService? missions, MissionReviewArgs request)
        {
            if (missions == null) return McpToolError.Unavailable("Review gates cannot be decided on this MCP server; use the Admiral's HTTP MCP endpoint.");
            if (String.IsNullOrWhiteSpace(request.MissionId)) return McpToolError.InvalidArgument("missionId is required.");
            AuthContext caller = McpToolHelpers.ResolveCallerContext();
            if (!McpToolHelpers.HasPresentedCredential() || CliPermissionAccess.IsCaptainSession(caller))
                return McpToolError.Forbidden("Deciding a review gate requires a person's credential; captain sessions and the unauthenticated loopback identity cannot.");
            Mission? mission = await McpCallerScope.ReadMissionAsync(database, caller, request.MissionId).ConfigureAwait(false);
            if (mission == null) return McpToolError.NotFound("Mission not found");
            if (mission.Status != MissionStatusEnum.Review) return McpToolError.Conflict("Mission " + mission.Id + " is " + mission.Status + ", not waiting for review.");
            return null;
        }

        #endregion
    }
}
