namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Applies the operator dispatch-preflight rule to a dispatch preview so every operator seam (MCP,
    /// REST, WebSocket) decides identically: an incomplete preflight is the one blocking issue an
    /// explicit force flag may override, and any other blocking issue is never overridden by it.
    /// </summary>
    public static class ObjectivePreflightGate
    {
        /// <summary>
        /// Stable code of the blocking issue an incomplete preflight adds to a dispatch preview.
        /// </summary>
        public const string IssueCode = "objective_preflight_incomplete";

        /// <summary>
        /// Event type recorded when an operator dispatches despite an incomplete preflight.
        /// </summary>
        public const string OverrideEventType = "objective.preflight_overridden";

        /// <summary>
        /// Build the objective event that records an operator's preflight override, so every operator
        /// seam records the same event shape.
        /// </summary>
        /// <param name="objective">Objective whose preflight was overridden.</param>
        /// <param name="operatorName">Name of the operator who forced the dispatch.</param>
        /// <returns>The event to persist.</returns>
        public static ArmadaEvent BuildOverrideEvent(Objective objective, string operatorName)
        {
            string numbers = String.Join(", ", ObjectivePreflightEvaluator.BlockingQuestions(objective.Preparation?.Preflight));
            return new ArmadaEvent(
                OverrideEventType,
                "Operator " + operatorName + " dispatched objective " + objective.Id
                    + " with an incomplete dispatch preflight; blocking question(s): " + numbers + ".")
            {
                TenantId = objective.TenantId,
                UserId = objective.UserId,
                EntityType = "objective",
                EntityId = objective.Id
            };
        }

        /// <summary>
        /// Resolve a stable operator name from an auth context for override attribution.
        /// </summary>
        /// <param name="auth">Caller context, or null.</param>
        /// <returns>The operator name, never empty.</returns>
        public static string OperatorName(AuthContext? auth)
        {
            if (auth == null) return "operator";
            if (!String.IsNullOrWhiteSpace(auth.PrincipalDisplay)) return auth.PrincipalDisplay!.Trim();
            if (!String.IsNullOrWhiteSpace(auth.UserId)) return auth.UserId!.Trim();
            if (!String.IsNullOrWhiteSpace(auth.AuthMethod)) return auth.AuthMethod!.Trim();
            return "operator";
        }

        /// <summary>
        /// Classify how an operator dispatch must treat the preview.
        /// </summary>
        /// <param name="preview">The dispatch preview to classify.</param>
        /// <param name="forcePreflight">Whether the operator asked to force past an incomplete preflight.</param>
        /// <returns>The gate outcome.</returns>
        public static PreflightGateOutcomeEnum Classify(ObjectiveDispatchPreview preview, bool forcePreflight)
        {
            // Readiness is the ground truth for "blocked"; the force flag only relaxes the single case
            // where an incomplete preflight is the only reason the preview is not ready. Anything else
            // that leaves the preview not ready refuses the dispatch, force flag or not.
            if (preview == null || preview.IsReady) return PreflightGateOutcomeEnum.Ready;

            List<ObjectiveDispatchPreviewIssue> errors = (preview.Issues ?? new List<ObjectiveDispatchPreviewIssue>())
                .Where(issue => issue != null && issue.Severity == ReadinessSeverityEnum.Error)
                .ToList();
            bool hasPreflightError = errors.Any(issue => issue.Code == IssueCode);
            bool hasOtherError = errors.Any(issue => issue.Code != IssueCode);

            if (hasOtherError || !hasPreflightError) return PreflightGateOutcomeEnum.BlockedByOther;
            return forcePreflight ? PreflightGateOutcomeEnum.OverriddenPreflight : PreflightGateOutcomeEnum.BlockedByPreflight;
        }
    }
}
