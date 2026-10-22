namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers the operator tool that resolves a stage handoff held by the <c>handoff_outcome</c> hold.
    /// The operator releases the stage, Mails the unmet criteria and releases it, or sends it to rescue;
    /// each choice is recorded as a labelled outcome of the decision. The model never resolves a hold.
    /// </summary>
    public static class McpHandoffHoldTools
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
        /// Register the handoff-hold tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="missions">Mission service that owns the hold transitions.</param>
        public static void Register(RegisterToolDelegate register, IMissionService missions)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (missions == null) throw new ArgumentNullException(nameof(missions));

            register(
                "armada_handoff_hold",
                "Resolve a stage handoff held by the handoff_outcome hold (inbox item kind handoff_outcome_hold). action=release hands the stage off unchanged; action=mail_release prepends the unmet-criteria note to the next stage's brief and hands it off; action=rescue fails the held stage and cancels its dependent stages so autonomous recovery decides a rescue. Each choice writes a typed_decision.labelled event joined to the decision on state_sha256. An unanswered hold is released with the note after the configured timeout. Operator-only: the model never resolves a hold.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", description = "release, mail_release or rescue" },
                        missionId = new { type = "string", description = "Held mission ID (msn_ prefix)" },
                        reason = new { type = "string", description = "Why this choice (required, recorded on the label)" },
                        @operator = new { type = "string", description = "Operator making the choice (required, recorded on the label)" }
                    },
                    required = new[] { "action", "missionId", "reason", "operator" }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!caller.IsAdmin)
                        return (object)new { Error = "Resolving a handoff-outcome hold requires a global administrator.", Reason = GlobalAdministratorRequiredReason };

                    HandoffHoldArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<HandoffHoldArgs>(args.Value, _JsonOptions) ?? new HandoffHoldArgs()
                        : new HandoffHoldArgs();

                    bool actionValid = HandoffOutcomeHold.TryParseChoice(request.Action, out HandoffOutcomeHoldChoiceEnum choice);
                    object? refusal = McpHoldArgumentCheck.Check(
                        actionValid, "action must be release, mail_release or rescue", request.MissionId, request.Operator, request.Reason);
                    if (refusal != null) return refusal;

                    try
                    {
                        Mission mission = await missions.ResolveHandoffOutcomeHoldAsync(
                            request.MissionId!.Trim(), choice, request.Operator!, request.Reason!).ConfigureAwait(false);
                        return (object)new
                        {
                            Action = HandoffOutcomeHold.LabelFor(choice),
                            MissionId = mission.Id,
                            Status = mission.Status.ToString(),
                            HeldForOperatorReview = mission.HeldForOperatorReview,
                            FailureReason = mission.FailureReason
                        };
                    }
                    catch (InvalidOperationException ex)
                    {
                        string reason = ex.Message.StartsWith("Mission not found", StringComparison.Ordinal)
                            ? "mission_not_found"
                            : ex.Message.StartsWith(MissionService.HandoffHoldBusyReason, StringComparison.Ordinal)
                                ? MissionService.HandoffHoldBusyReason
                                : "not_held";
                        return (object)new { Error = ex.Message, Reason = reason };
                    }
                });
        }

        #endregion

        #region Private-Types

        private sealed class HandoffHoldArgs
        {
            public string? Action { get; set; } = null;

            public string? MissionId { get; set; } = null;

            public string? Reason { get; set; } = null;

            public string? Operator { get; set; } = null;
        }

        #endregion
    }
}
