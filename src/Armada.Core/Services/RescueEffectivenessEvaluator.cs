namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Judges a rescue by what it CHANGED rather than by whether it ran.
    /// </summary>
    /// <remarks>
    /// A rescue that starts, logs, and exits looks successful to every measure the platform keeps:
    /// the process lived, the captain reported, the pipeline advanced. None of that says the
    /// defect was touched. One rescue ran for twenty-four hours, drew escalating stall nudges,
    /// died on a runtime crash, and left behind a single changed documentation file - and the only
    /// reason anyone noticed was that a human read the diff.
    /// <para>
    /// The evaluation is deliberately narrow. It asks one question - could this change set
    /// possibly have fixed anything? - and answers it from the changed paths alone. It does not
    /// compare the rescue's paths against the original mission's: a rescue is EXPECTED to rewrite
    /// the prior branch from scratch over the same files, so treating an overlapping path set as
    /// evidence of a no-op would flag the normal case.
    /// </para>
    /// </remarks>
    public static class RescueEffectivenessEvaluator
    {
        #region Public-Methods

        /// <summary>
        /// Assess whether a rescue produced work capable of addressing its defect.
        /// </summary>
        /// <param name="changedPaths">Repository-relative paths the rescue changed.</param>
        /// <param name="requiresCodeChange">
        /// Whether the mission's mode requires a commit. Audit and Research missions deliver a
        /// report and are never expected to change code, so they are never flagged - judging them
        /// by a diff is the same mistake in the other direction.
        /// </param>
        /// <returns>The assessment, including a reason suitable for a failure record.</returns>
        public static RescueEffectivenessAssessment Assess(
            IEnumerable<string>? changedPaths,
            bool requiresCodeChange)
        {
            ChangeSubstanceEnum substance = ChangeSubstanceClassifier.Classify(changedPaths);

            if (!requiresCodeChange)
            {
                return new RescueEffectivenessAssessment(
                    false,
                    substance,
                    "Report-only mission; a change set is not expected and is not evidence either way.");
            }

            switch (substance)
            {
                case ChangeSubstanceEnum.None:
                    return new RescueEffectivenessAssessment(
                        true,
                        substance,
                        "The rescue produced no changes at all, so the defect it was dispatched for cannot have been addressed.");

                case ChangeSubstanceEnum.DocumentationOnly:
                    return new RescueEffectivenessAssessment(
                        true,
                        substance,
                        "The rescue changed only documentation, so the defect it was dispatched for was described rather than fixed.");

                default:
                    return new RescueEffectivenessAssessment(
                        false,
                        substance,
                        "The rescue changed at least one file that can carry behavior.");
            }
        }

        #endregion
    }

    /// <summary>
    /// The result of judging a rescue by its change set.
    /// </summary>
    public sealed class RescueEffectivenessAssessment
    {
        #region Public-Members

        /// <summary>
        /// Whether the rescue ran without producing work that could address its defect.
        /// </summary>
        public bool IsIneffective { get; }

        /// <summary>
        /// What the change set consisted of.
        /// </summary>
        public ChangeSubstanceEnum Substance { get; }

        /// <summary>
        /// Why the assessment reached its verdict, phrased for a failure record an operator reads.
        /// </summary>
        public string Reason { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate an assessment.
        /// </summary>
        /// <param name="isIneffective">Whether the rescue produced no usable work.</param>
        /// <param name="substance">The classified substance of the change set.</param>
        /// <param name="reason">Operator-facing explanation.</param>
        public RescueEffectivenessAssessment(bool isIneffective, ChangeSubstanceEnum substance, string reason)
        {
            IsIneffective = isIneffective;
            Substance = substance;
            Reason = reason ?? String.Empty;
        }

        #endregion
    }
}
