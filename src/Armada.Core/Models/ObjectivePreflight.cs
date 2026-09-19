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
        public const int QuestionCount = 14;

        /// <summary>
        /// Question 13, answered yes only when an open owner question exists. A yes here blocks
        /// dispatch; questions 1-12 and 14 block when they are not answered yes. Number 13 stays
        /// stable because corpus lines and recorded answers already cite it.
        /// </summary>
        public const int OwnerQuestionNumber = 13;

        /// <summary>
        /// Question 14, numbered last so 1-13 stay stable: the target tip is green, or the brief names
        /// the failures it inherits. This is a recorded operator answer, not a live suite run at
        /// preview time. Preview reports an unanswered or no answer the same way it reports questions
        /// 1-12. Measuring the tip is the operator's job before they record yes.
        /// </summary>
        public const int GreenTipQuestionNumber = 14;

        /// <summary>
        /// Recorded answers, at most one per question number.
        /// </summary>
        public List<ObjectivePreflightAnswer> Questions { get; set; } = new List<ObjectivePreflightAnswer>();
    }
}
