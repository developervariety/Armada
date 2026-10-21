namespace Armada.Core.Services.TypedDecisions
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The outcome join for one decision, or for one confidence band of one decision: how many recorded calls
    /// name a subject, how many subjects reached a known outcome, and what that outcome was. A mission subject
    /// reaches an outcome when it is terminal; an objective subject when it is completed or cancelled. The
    /// landing result, a linked incident and a rescue are counted per call, so a decision's answers can be
    /// scored against what happened.
    /// </summary>
    public sealed class TypedDecisionOutcomeRow
    {
        /// <summary>The decision point, or the band label for a band row.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Recorded calls in the window.</summary>
        public int Events { get; set; } = 0;

        /// <summary>Calls that name a subject record.</summary>
        public int WithSubject { get; set; } = 0;

        /// <summary>Calls whose subject reached a known outcome.</summary>
        public int Joined { get; set; } = 0;

        /// <summary>Calls whose subject record no longer exists.</summary>
        public int SubjectMissing { get; set; } = 0;

        /// <summary>Calls whose mission subject landed (Complete).</summary>
        public int Landed { get; set; } = 0;

        /// <summary>Calls whose mission subject failed or failed to land.</summary>
        public int Failed { get; set; } = 0;

        /// <summary>Calls whose subject was cancelled.</summary>
        public int Cancelled { get; set; } = 0;

        /// <summary>Calls whose objective subject completed.</summary>
        public int ObjectiveCompleted { get; set; } = 0;

        /// <summary>Calls whose mission subject has at least one linked incident.</summary>
        public int WithIncident { get; set; } = 0;

        /// <summary>Calls whose mission subject was rescued: a later mission names it as its parent.</summary>
        public int Rescued { get; set; } = 0;

        /// <summary>Share of calls that name a subject, in [0, 1]; null with no calls.</summary>
        public double? SubjectShare => Events == 0 ? null : Math.Round((double)WithSubject / Events, 4);

        /// <summary>Share of calls whose subject reached a known outcome, in [0, 1]; null with no calls.</summary>
        public double? JoinedShare => Events == 0 ? null : Math.Round((double)Joined / Events, 4);

        /// <summary>Calls per subject type (for example mission, objective, papercut_group).</summary>
        public Dictionary<string, int> BySubjectType { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Calls per recorded event type.</summary>
        public Dictionary<string, int> ByEventType { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// The same join split by the model's action confidence, so accuracy by confidence band is computable
        /// where the decision's correct answer follows from the outcome. Empty on a band row.
        /// </summary>
        public List<TypedDecisionOutcomeRow> ByConfidenceBand { get; set; } = new List<TypedDecisionOutcomeRow>();
    }
}
