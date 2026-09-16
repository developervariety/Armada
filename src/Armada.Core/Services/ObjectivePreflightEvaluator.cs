namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Evaluates the recorded dispatch-preflight answers on an objective. This is the single place the
    /// blocking rule lives: every other gate (autonomous scheduler, operator dispatch, dispatch
    /// preview) consumes this result rather than re-deriving the rule. The rule itself is the operator
    /// dispatch-preflight battery; this evaluator enforces that each answer is recorded and admits
    /// dispatch, without restating the battery's questions.
    /// </summary>
    public static class ObjectivePreflightEvaluator
    {
        /// <summary>
        /// Return the numbers of the battery questions that block dispatch, in ascending order. A
        /// question blocks when it is unanswered, when a question that must be yes is answered no, or
        /// when the open-owner-question question is answered yes. An empty result means dispatch is
        /// admitted by the recorded answers.
        /// </summary>
        /// <param name="preflight">Recorded preflight answers, or null when none are recorded.</param>
        /// <returns>Ascending list of blocking question numbers.</returns>
        public static IReadOnlyList<int> BlockingQuestions(ObjectivePreflight? preflight)
        {
            Dictionary<int, ObjectivePreflightAnswerEnum> answers = new Dictionary<int, ObjectivePreflightAnswerEnum>();
            foreach (ObjectivePreflightAnswer answer in preflight?.Questions ?? new List<ObjectivePreflightAnswer>())
            {
                if (answer == null) continue;
                if (answer.Number < 1 || answer.Number > ObjectivePreflight.QuestionCount) continue;
                answers[answer.Number] = answer.Answer;
            }

            List<int> blocking = new List<int>();
            for (int number = 1; number <= ObjectivePreflight.QuestionCount; number++)
            {
                ObjectivePreflightAnswerEnum answer = answers.TryGetValue(number, out ObjectivePreflightAnswerEnum recorded)
                    ? recorded
                    : ObjectivePreflightAnswerEnum.Unanswered;

                if (answer == ObjectivePreflightAnswerEnum.Unanswered)
                {
                    blocking.Add(number);
                    continue;
                }

                if (number == ObjectivePreflight.OwnerQuestionNumber)
                {
                    // The open-owner-question question is answered no when there is no open question.
                    if (answer == ObjectivePreflightAnswerEnum.Yes) blocking.Add(number);
                }
                else if (answer == ObjectivePreflightAnswerEnum.No)
                {
                    blocking.Add(number);
                }
            }

            return blocking;
        }

        /// <summary>
        /// Whether the recorded answers admit dispatch.
        /// </summary>
        /// <param name="preflight">Recorded preflight answers, or null when none are recorded.</param>
        /// <returns>True when no question blocks dispatch.</returns>
        public static bool IsComplete(ObjectivePreflight? preflight)
        {
            return BlockingQuestions(preflight).Count == 0;
        }

        /// <summary>
        /// The recorded answer for one battery question, or unanswered when none is recorded.
        /// </summary>
        /// <param name="preflight">Recorded preflight answers, or null.</param>
        /// <param name="number">Battery question number.</param>
        /// <returns>The recorded answer, or unanswered.</returns>
        public static ObjectivePreflightAnswerEnum RecordedAnswer(ObjectivePreflight? preflight, int number)
        {
            ObjectivePreflightAnswer? match = (preflight?.Questions ?? new List<ObjectivePreflightAnswer>())
                .FirstOrDefault(answer => answer != null && answer.Number == number);
            return match?.Answer ?? ObjectivePreflightAnswerEnum.Unanswered;
        }
    }
}
