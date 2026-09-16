namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One operator answer to a numbered dispatch-preflight question. The set of questions is the
    /// operator dispatch-preflight battery; this record stores only the answer, not the question text.
    /// </summary>
    public class ObjectivePreflightAnswer
    {
        /// <summary>
        /// Question number in the battery, from 1 to the battery size.
        /// </summary>
        public int Number { get; set; } = 0;

        /// <summary>
        /// Recorded answer. Unanswered until an operator records one.
        /// </summary>
        public ObjectivePreflightAnswerEnum Answer { get; set; } = ObjectivePreflightAnswerEnum.Unanswered;

        /// <summary>
        /// Operator note supporting the answer, for example the grep the answer rests on.
        /// </summary>
        public string Note { get; set; } = String.Empty;

        /// <summary>
        /// When the answer was recorded, in UTC.
        /// </summary>
        public DateTime? AnsweredUtc { get; set; } = null;

        /// <summary>
        /// Who recorded the answer.
        /// </summary>
        public string AnsweredBy { get; set; } = String.Empty;
    }
}
