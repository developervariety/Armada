namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Applies the operator dispatch-preflight rule to a dispatch preview so every operator seam (MCP,
    /// REST, WebSocket) decides identically: the preflight-class blocking issues -- an incomplete
    /// preflight and a D5 preflight model flag -- are the only blocking issues an explicit force flag may
    /// override, and any other blocking issue is never overridden by it.
    /// </summary>
    public static class ObjectivePreflightGate
    {
        /// <summary>
        /// Stable code of the blocking issue an incomplete preflight adds to a dispatch preview.
        /// </summary>
        public const string IssueCode = "objective_preflight_incomplete";

        /// <summary>
        /// Event type recorded when an operator dispatches past a preflight-class blocking issue.
        /// </summary>
        public const string OverrideEventType = "objective.preflight_overridden";

        /// <summary>
        /// Refusal message every operator seam returns when a preflight-class issue blocks a dispatch
        /// that did not set the force flag.
        /// </summary>
        public const string RefusalMessage = "Objective dispatch preflight is incomplete or model-flagged. Complete it, or set forcePreflight to override.";

        /// <summary>
        /// Whether a preview issue code is preflight-class: an incomplete preflight or a D5 preflight
        /// model flag. Only these codes may be overridden by the operator force flag.
        /// </summary>
        /// <param name="code">Issue code.</param>
        /// <returns>True when the force flag may override the issue.</returns>
        public static bool IsPreflightClassCode(string? code)
        {
            return String.Equals(code, IssueCode, StringComparison.Ordinal)
                || String.Equals(code, PreflightTextAdapter.ModelFlagIssueCode, StringComparison.Ordinal);
        }

        /// <summary>
        /// The question numbers the D5 preflight model flagged in a preview, ascending and distinct.
        /// </summary>
        /// <param name="preview">The dispatch preview, or null.</param>
        /// <returns>The flagged question numbers; empty when none.</returns>
        public static List<int> ModelFlaggedQuestions(ObjectiveDispatchPreview? preview)
        {
            SortedSet<int> numbers = new SortedSet<int>();
            foreach (ObjectiveDispatchPreviewIssue issue in preview?.Issues ?? new List<ObjectiveDispatchPreviewIssue>())
            {
                if (issue == null || !String.Equals(issue.Code, PreflightTextAdapter.ModelFlagIssueCode, StringComparison.Ordinal)) continue;
                string related = (issue.RelatedValue ?? String.Empty).Trim();
                if (related.StartsWith("q", StringComparison.OrdinalIgnoreCase)) related = related.Substring(1);
                if (Int32.TryParse(related, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                    numbers.Add(number);
            }
            return numbers.ToList();
        }

        /// <summary>
        /// Build the objective event that records an operator's preflight override, so every operator
        /// seam records the same event shape. The message names the recorded blocking questions and the
        /// questions the D5 preflight model flagged.
        /// </summary>
        /// <param name="objective">Objective whose preflight was overridden.</param>
        /// <param name="operatorName">Name of the operator who forced the dispatch.</param>
        /// <param name="modelFlaggedQuestions">Question numbers the D5 preflight model flagged, or null when none.</param>
        /// <returns>The event to persist.</returns>
        public static ArmadaEvent BuildOverrideEvent(Objective objective, string operatorName, IReadOnlyList<int>? modelFlaggedQuestions = null)
        {
            IReadOnlyList<int> blocking = ObjectivePreflightEvaluator.BlockingQuestions(objective.Preparation?.Preflight);
            IReadOnlyList<int> flagged = modelFlaggedQuestions ?? new List<int>();
            string message = "Operator " + operatorName + " dispatched objective " + objective.Id + " past the dispatch preflight";
            if (blocking.Count > 0) message += "; blocking question(s): " + String.Join(", ", blocking);
            if (flagged.Count > 0) message += "; model-flagged question(s): " + String.Join(", ", flagged);
            return new ArmadaEvent(OverrideEventType, message + ".")
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
        /// <param name="forcePreflight">Whether the operator asked to force past preflight-class issues.</param>
        /// <returns>The gate outcome.</returns>
        public static PreflightGateOutcomeEnum Classify(ObjectiveDispatchPreview preview, bool forcePreflight)
        {
            // Readiness is the ground truth for "blocked"; the force flag only relaxes the case where
            // preflight-class issues are the only reason the preview is not ready. Anything else that
            // leaves the preview not ready refuses the dispatch, force flag or not.
            if (preview == null || preview.IsReady) return PreflightGateOutcomeEnum.Ready;

            List<ObjectiveDispatchPreviewIssue> errors = (preview.Issues ?? new List<ObjectiveDispatchPreviewIssue>())
                .Where(issue => issue != null && issue.Severity == ReadinessSeverityEnum.Error)
                .ToList();
            bool hasPreflightError = errors.Any(issue => IsPreflightClassCode(issue.Code));
            bool hasOtherError = errors.Any(issue => !IsPreflightClassCode(issue.Code));

            if (hasOtherError || !hasPreflightError) return PreflightGateOutcomeEnum.BlockedByOther;
            return forcePreflight ? PreflightGateOutcomeEnum.OverriddenPreflight : PreflightGateOutcomeEnum.BlockedByPreflight;
        }
    }
}
