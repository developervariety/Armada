namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// The one egress guard. Every path that sends decision state off the host - the adapter skeleton, the
    /// standalone adapters, the custom decisions and the captain tools - asks <see cref="Refusal(TypedDecisionSettings, IReadOnlyList{string}, IEnumerable{string}, Func{object})"/>
    /// before it sends, so the rule cannot drift between them. The vessel rule - every objective, mission and
    /// vessel the decision concerns - is asked first, before any state is built. The marker rule reads the state BEFORE redaction, as text, because the redactor replaces absolute
    /// workspace paths - where dock and sibling-checkout paths live - with a placeholder, taking a marker inside
    /// them along.
    /// </summary>
    public static class TypedDecisionEgress
    {
        /// <summary>The unavailable reason a content-marker exclusion records.</summary>
        public const string ExcludedContentReason = "egress_excluded_content";

        /// <summary>The unavailable reason a vessel exclusion records.</summary>
        public const string ExcludedVesselReason = "egress_excluded_vessel";

        /// <summary>
        /// Why a state may not leave the host, or null when it may. A decision about an objective, mission or vessel
        /// that names a vessel on <see cref="TypedDecisionSettings.EgressExcludedVesselIds"/> returns
        /// <see cref="ExcludedVesselReason"/> without building the state; a state whose unredacted text contains one
        /// of <paramref name="markers"/> returns <see cref="ExcludedContentReason"/>. A state that cannot be built or
        /// read counts as carrying a marker: when the check cannot run, nothing is sent.
        /// </summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="markers">The markers that apply to this decision.</param>
        /// <param name="vesselIds">Every vessel the decision concerns: the mission's, the target vessel, the objective's
        /// vessels. Null or empty when it concerns none.</param>
        /// <param name="buildState">Builds the unredacted state; called only when markers apply.</param>
        /// <returns>The refusal reason, or null.</returns>
        public static string? Refusal(TypedDecisionSettings settings, IReadOnlyList<string> markers, IEnumerable<string?>? vesselIds, Func<object?> buildState)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (vesselIds != null)
            {
                foreach (string? vesselId in vesselIds)
                    if (!settings.AllowsEgress(vesselId)) return ExcludedVesselReason;
            }
            if (markers == null || markers.Count == 0) return null;

            string raw;
            try
            {
                raw = RawText(buildState());
            }
            catch (Exception)
            {
                raw = ((char)0).ToString();
            }
            return TypedDecisionSettings.FirstMarkerIn(markers, raw) != null ? ExcludedContentReason : null;
        }

        /// <summary>The guard for a built-in decision, with the markers that apply to it.</summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="decisionPoint">The built-in decision.</param>
        /// <param name="vesselIds">Every vessel the decision concerns; null or empty when it concerns none.</param>
        /// <param name="buildState">Builds the unredacted state.</param>
        /// <returns>The refusal reason, or null.</returns>
        public static string? Refusal(TypedDecisionSettings settings, string decisionPoint, IEnumerable<string?>? vesselIds, Func<object?> buildState)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return Refusal(settings, settings.MarkersFor(decisionPoint), vesselIds, buildState);
        }

        /// <summary>The guard for a built-in decision about at most one vessel.</summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="decisionPoint">The built-in decision.</param>
        /// <param name="vesselId">The vessel the decision concerns, or null.</param>
        /// <param name="buildState">Builds the unredacted state.</param>
        /// <returns>The refusal reason, or null.</returns>
        public static string? Refusal(TypedDecisionSettings settings, string decisionPoint, string? vesselId, Func<object?> buildState)
        {
            return Refusal(settings, decisionPoint, new[] { vesselId }, buildState);
        }

        /// <summary>
        /// The vessels an objective-scoped decision concerns: the objective's own vessels plus any further ids
        /// (the resolved target vessel).
        /// </summary>
        /// <param name="objectiveVesselIds">The objective's vessel ids; null for none.</param>
        /// <param name="more">Further vessel ids; nulls are ignored.</param>
        /// <returns>The ids.</returns>
        public static List<string?> VesselsOf(IEnumerable<string>? objectiveVesselIds, params string?[] more)
        {
            List<string?> ids = new List<string?>();
            if (objectiveVesselIds != null) ids.AddRange(objectiveVesselIds);
            if (more != null) ids.AddRange(more);
            return ids;
        }

        /// <summary>The unavailable result a refused call records in place of a provider answer.</summary>
        /// <param name="reason">The refusal reason.</param>
        /// <returns>An unavailable result carrying the reason.</returns>
        public static TypedDecisionResult Refused(string reason)
        {
            return new TypedDecisionResult { Available = false, UnavailableReason = reason };
        }

        /// <summary>
        /// Whether a result is an egress refusal rather than a provider failure. A refusal belongs to one item, so
        /// a pass over several items records it and goes on; a provider failure ends the pass.
        /// </summary>
        /// <param name="result">The result.</param>
        /// <returns>True for a refusal.</returns>
        public static bool IsRefusal(TypedDecisionResult? result)
        {
            if (result == null || result.Available) return false;
            return String.Equals(result.UnavailableReason, ExcludedVesselReason, StringComparison.Ordinal)
                || String.Equals(result.UnavailableReason, ExcludedContentReason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Decide the items that may be sent, in as few requests as the limits allow, and return one result per
        /// item in order: a refused item (its <paramref name="refusals"/> entry set, its item null) gets
        /// <see cref="Refused"/> and is never sent.
        /// </summary>
        /// <param name="client">The typed-decision client.</param>
        /// <param name="decisionPoint">The decision every item belongs to.</param>
        /// <param name="items">The prepared items; null where refused.</param>
        /// <param name="refusals">The refusal reason per item; null where the item may be sent.</param>
        /// <param name="maxStateChars">The per-request state budget.</param>
        /// <param name="token">Cancellation token, forwarded to the client.</param>
        /// <returns>One result per item, in order.</returns>
        public static async Task<List<TypedDecisionResult>> DecideAllowedAsync(
            ITypedDecisionClient client,
            string decisionPoint,
            IReadOnlyList<TypedDecisionBatchItem?> items,
            IReadOnlyList<string?> refusals,
            int maxStateChars,
            CancellationToken token)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (refusals == null || refusals.Count != items.Count) throw new ArgumentException("Each item needs one refusal entry.", nameof(refusals));

            List<TypedDecisionBatchItem> allowed = new List<TypedDecisionBatchItem>();
            for (int index = 0; index < items.Count; index++)
                if (refusals[index] == null && items[index] != null) allowed.Add(items[index]!);

            List<TypedDecisionResult> answered = allowed.Count == 0
                ? new List<TypedDecisionResult>()
                : await TypedDecisionBatcher.DecideAllAsync(client, decisionPoint, allowed, maxStateChars, token).ConfigureAwait(false);

            List<TypedDecisionResult> results = new List<TypedDecisionResult>(items.Count);
            int next = 0;
            for (int index = 0; index < items.Count; index++)
            {
                if (refusals[index] != null) results.Add(Refused(refusals[index]!));
                else if (items[index] == null) results.Add(TypedDecisionResult.Exception());
                else results.Add(next < answered.Count ? answered[next++] : TypedDecisionResult.Exception());
            }
            return results;
        }

        /// <summary>The raw state as text, exactly as the check reads it.</summary>
        /// <param name="state">The state before redaction.</param>
        /// <returns>The text; empty for no state.</returns>
        public static string RawText(object? state)
        {
            if (state == null) return String.Empty;
            if (state is string text) return text;
            try
            {
                return JsonSerializer.Serialize(state);
            }
            catch (Exception)
            {
                // A state that cannot be read cannot be cleared either; the caller treats it as excluded.
                return ((char)0).ToString();
            }
        }

        /// <summary>Whether the raw text could not be produced, which a caller must treat as excluded.</summary>
        /// <param name="rawText">Text from <see cref="RawText"/>.</param>
        /// <returns>True when the state was unreadable.</returns>
        public static bool IsUnreadable(string rawText) => rawText.Length == 1 && rawText[0] == (char)0;
    }
}
