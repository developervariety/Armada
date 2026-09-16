namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// The dispatch-preflight section of an objective dispatch preview: whether the recorded answers
    /// admit dispatch, which question numbers still block it, and the facts the code determined for
    /// itself so a recorded answer can be checked against the repository.
    /// </summary>
    public class ObjectiveDispatchPreflight
    {
        /// <summary>
        /// True when every battery question is answered in a way that admits dispatch.
        /// </summary>
        public bool IsComplete { get; set; } = false;

        /// <summary>
        /// Question numbers that block dispatch: any unanswered question, a no on a question that must
        /// be yes, or a yes on the open-owner-question question.
        /// </summary>
        public List<int> IncompleteQuestions { get; set; } = new List<int>();

        /// <summary>
        /// Facts the code determined for the deterministic questions, one per determinable question.
        /// </summary>
        public List<ObjectiveDispatchPreflightFact> Facts { get; set; } = new List<ObjectiveDispatchPreflightFact>();
    }
}
