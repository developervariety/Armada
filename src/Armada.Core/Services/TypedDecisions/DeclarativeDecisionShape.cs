namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Turns a declarative decision definition into the questions and state a decision sends. A built-in
    /// decision with an embedded definition and a custom decision are asked the same way through this one
    /// engine: a question setting becomes a Choice, Score, or Noul, and a state is the named context
    /// fields in order. It reads definitions; it never gates and never produces a verdict.
    /// </summary>
    public static class DeclarativeDecisionShape
    {
        /// <summary>
        /// Build the typed questions from a definition's question settings, keyed by id, in order. A
        /// question with a blank id or blank instructions is skipped.
        /// </summary>
        /// <param name="questions">The declarative question settings.</param>
        /// <returns>The questions keyed by id.</returns>
        public static IReadOnlyDictionary<string, TypedQuestion> BuildQuestions(IEnumerable<CustomTypedQuestionSettings> questions)
        {
            Dictionary<string, TypedQuestion> built = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            if (questions == null) return built;
            foreach (CustomTypedQuestionSettings question in questions)
            {
                if (question == null || String.IsNullOrWhiteSpace(question.Id) || String.IsNullOrWhiteSpace(question.Instructions)) continue;
                built[question.Id] = ToQuestion(question);
            }
            return built;
        }

        /// <summary>
        /// Assemble the state from a context by selecting the named fields, in order. Absent or null
        /// fields are skipped. An empty field list selects nothing, so a caller that wants a default set
        /// passes it explicitly.
        /// </summary>
        /// <param name="fields">The field names to serialize, in order.</param>
        /// <param name="context">The available field values.</param>
        /// <returns>The state object.</returns>
        public static object BuildState(IReadOnlyList<string> fields, IReadOnlyDictionary<string, object?> context)
        {
            Dictionary<string, object?> state = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (fields == null || context == null) return state;
            foreach (string field in fields)
            {
                if (String.IsNullOrWhiteSpace(field)) continue;
                if (context.TryGetValue(field, out object? value) && value != null) state[field] = value;
            }
            return state;
        }

        private static TypedQuestion ToQuestion(CustomTypedQuestionSettings question)
        {
            switch ((question.Type ?? "noul").Trim().ToLowerInvariant())
            {
                case "choice":
                    return new ChoiceQuestion(question.Instructions, question.Options);
                case "score":
                    return new ScoreQuestion(question.Instructions, question.Levels);
                default:
                    return new NoulQuestion(question.Instructions, question.TrueMeaning, question.FalseMeaning);
            }
        }
    }
}
