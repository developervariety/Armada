namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers the operator tool that resolves a Judge PASS held for operator review. Clearing lets the
    /// held PASS proceed through the normal handoff or landing path; failing fails the mission. The model
    /// never resolves a hold, and nothing resolves one automatically.
    /// </summary>
    public static class McpReviewHoldTools
    {
        #region Public-Members

        /// <summary>Refusal reason returned when a caller other than a global administrator calls the tool.</summary>
        public const string GlobalAdministratorRequiredReason = "global_administrator_required";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the review-hold tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="missions">Mission service that owns the hold transitions.</param>
        public static void Register(RegisterToolDelegate register, IMissionService missions)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (missions == null) throw new ArgumentNullException(nameof(missions));

            register(
                "armada_review_hold",
                "Resolve a Judge PASS held for operator review (inbox item kind judge_pass_held). action=clear releases the hold and the PASS proceeds through the normal handoff or landing path; action=fail fails the mission and cancels its dependent stages. Each writes a mission.hold_cleared or mission.hold_failed event naming the operator and reason. Operator-only: the model never approves a hold and nothing clears one automatically.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", description = "clear or fail" },
                        missionId = new { type = "string", description = "Held mission ID (msn_ prefix)" },
                        reason = new { type = "string", description = "Why the hold is cleared or failed (required, recorded on the event)" },
                        @operator = new { type = "string", description = "Operator making the decision (required, recorded on the event)" }
                    },
                    required = new[] { "action", "missionId", "reason", "operator" }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!caller.IsAdmin)
                        return (object)new { Error = "Resolving an operator review hold requires a global administrator.", Reason = GlobalAdministratorRequiredReason };

                    ReviewHoldArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<ReviewHoldArgs>(args.Value, _JsonOptions) ?? new ReviewHoldArgs()
                        : new ReviewHoldArgs();

                    string action = (request.Action ?? String.Empty).Trim().ToLowerInvariant();
                    if (action != "clear" && action != "fail")
                        return (object)new { Error = "action must be clear or fail.", Reason = "invalid_action" };
                    if (String.IsNullOrWhiteSpace(request.MissionId))
                        return (object)new { Error = "missionId is required.", Reason = "missing_mission_id" };
                    if (String.IsNullOrWhiteSpace(request.Operator))
                        return (object)new { Error = "operator is required.", Reason = "missing_operator" };
                    if (String.IsNullOrWhiteSpace(request.Reason))
                        return (object)new { Error = "reason is required.", Reason = "missing_reason" };

                    try
                    {
                        Mission mission = action == "clear"
                            ? await missions.ClearOperatorReviewHoldAsync(request.MissionId!.Trim(), request.Operator!, request.Reason!).ConfigureAwait(false)
                            : await missions.FailOperatorReviewHoldAsync(request.MissionId!.Trim(), request.Operator!, request.Reason!).ConfigureAwait(false);
                        return (object)new
                        {
                            Action = action,
                            MissionId = mission.Id,
                            Status = mission.Status.ToString(),
                            HeldForOperatorReview = mission.HeldForOperatorReview,
                            FailureReason = mission.FailureReason
                        };
                    }
                    catch (InvalidOperationException ex)
                    {
                        string reason = ex.Message.StartsWith("Mission not found", StringComparison.Ordinal) ? "mission_not_found" : "not_held";
                        return (object)new { Error = ex.Message, Reason = reason };
                    }
                });
        }

        #endregion

        #region Private-Types

        private sealed class ReviewHoldArgs
        {
            public string? Action { get; set; } = null;

            public string? MissionId { get; set; } = null;

            public string? Reason { get; set; } = null;

            public string? Operator { get; set; } = null;
        }

        #endregion
    }
}
