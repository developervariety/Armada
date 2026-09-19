namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds dispatch-preflight answer sets for tests that need an objective to pass or fail the
    /// preflight gate without restating the battery in every test.
    /// </summary>
    public static class PreflightTestData
    {
        /// <summary>
        /// A complete preflight: every question that must be yes is yes (1-12 and 14), and the
        /// open-owner question (13) is no. An objective carrying this is admitted by the preflight gate.
        /// </summary>
        /// <returns>A complete preflight answer set.</returns>
        public static ObjectivePreflight Complete()
        {
            List<ObjectivePreflightAnswer> answers = new List<ObjectivePreflightAnswer>();
            for (int number = 1; number <= ObjectivePreflight.QuestionCount; number++)
            {
                answers.Add(new ObjectivePreflightAnswer
                {
                    Number = number,
                    Answer = number == ObjectivePreflight.OwnerQuestionNumber
                        ? ObjectivePreflightAnswerEnum.No
                        : ObjectivePreflightAnswerEnum.Yes,
                    Note = "verified",
                    AnsweredUtc = DateTime.UtcNow,
                    AnsweredBy = "UnitTest"
                });
            }
            return new ObjectivePreflight { Questions = answers };
        }
    }
}
