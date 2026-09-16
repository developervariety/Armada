namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A preflight question the code determined for itself, shown in the dispatch preview so the
    /// operator's recorded answer can be checked against the repository. The fact is informational and
    /// does not block dispatch; only the recorded answers do.
    /// </summary>
    public class ObjectiveDispatchPreflightFact
    {
        /// <summary>
        /// Battery question number this fact answers.
        /// </summary>
        public int QuestionNumber { get; set; } = 0;

        /// <summary>
        /// What the repository says about the question.
        /// </summary>
        public PreflightFactStatusEnum Status { get; set; } = PreflightFactStatusEnum.Unknown;

        /// <summary>
        /// Human-readable explanation of how the fact was determined.
        /// </summary>
        public string Detail { get; set; } = String.Empty;

        /// <summary>
        /// The operator's recorded answer for the same question, echoed for comparison.
        /// </summary>
        public ObjectivePreflightAnswerEnum RecordedAnswer { get; set; } = ObjectivePreflightAnswerEnum.Unanswered;
    }
}
