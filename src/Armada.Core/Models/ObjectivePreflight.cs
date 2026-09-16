namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Operator answers to the dispatch-preflight battery, stored on an objective's preparation.
    /// Dispatch is refused while any question is unanswered, so an objective with no recorded answers
    /// is not dispatchable.
    /// </summary>
    public class ObjectivePreflight
    {
        /// <summary>
        /// The number of questions in the dispatch-preflight battery.
        /// </summary>
        public const int QuestionCount = 13;

        /// <summary>
        /// The last battery question, answered yes only when an open owner question exists. A yes here
        /// blocks dispatch; every earlier question blocks when it is not answered yes.
        /// </summary>
        public const int OwnerQuestionNumber = 13;

        /// <summary>
        /// Recorded answers, at most one per question number.
        /// </summary>
        public List<ObjectivePreflightAnswer> Questions { get; set; } = new List<ObjectivePreflightAnswer>();
    }
}
