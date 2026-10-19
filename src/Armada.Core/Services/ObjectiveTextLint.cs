namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// Finds sentences in a stored objective that contradict the record or each other. A long-running
    /// objective accumulates appended paragraphs, and an older sentence that names a different current,
    /// parent or authorizing objective, or a different criterion count, stays beside its correction; a
    /// captain then spends turns deciding which sentence controls. The record's own id and
    /// <see cref="Objective.ParentObjectiveId"/> are the authority, and the acceptance criteria list is
    /// the count.
    /// </summary>
    public static class ObjectiveTextLint
    {
        #region Public-Members

        /// <summary>
        /// The most sentences one finding quotes.
        /// </summary>
        public const int MaxSentences = 5;

        /// <summary>
        /// The longest quoted sentence; longer ones are cut with an ellipsis.
        /// </summary>
        public const int MaxSentenceLength = 200;

        #endregion

        #region Private-Members

        private static readonly Regex _ObjectiveId = new Regex(@"\bobj_[a-z0-9]{8}(?:_[A-Za-z0-9]+)?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _RoleWord = new Regex(@"\b(?:current|parent|authori[sz]\w*|owner|root|controlling)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _CriterionCount = new Regex(
            @"\b(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+(?:acceptance\s+)?criteria\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _SentenceBreak = new Regex(@"(?<=[.!?])\s+|\r?\n+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] _NumberWords = new string[]
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Sentences that call an objective other than this one or its stored parent current, parent,
        /// authorizing, owning or controlling.
        /// </summary>
        /// <param name="objective">The stored objective.</param>
        /// <returns>The conflicting sentences, bounded; empty when none.</returns>
        public static List<string> FindConflictingObjectiveIds(Objective objective)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            List<string> sentences = new List<string>();
            foreach (string sentence in Sentences(objective))
            {
                if (!_RoleWord.IsMatch(sentence)) continue;
                bool conflicts = _ObjectiveId.Matches(sentence)
                    .Select(match => match.Value)
                    .Any(id => !SameObjective(id, objective.Id) && !SameObjective(id, objective.ParentObjectiveId));
                if (conflicts) AddBounded(sentences, sentence);
            }
            return sentences;
        }

        /// <summary>
        /// Sentences that state a criterion count: all of them when they state different counts, or the
        /// one that disagrees with the acceptance criteria list when they agree with each other.
        /// </summary>
        /// <param name="objective">The stored objective.</param>
        /// <returns>The conflicting sentences, bounded; empty when none.</returns>
        public static List<string> FindConflictingCriterionCounts(Objective objective)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            List<KeyValuePair<int, string>> stated = new List<KeyValuePair<int, string>>();
            foreach (string sentence in Sentences(objective))
            {
                foreach (Match match in _CriterionCount.Matches(sentence))
                {
                    int count = ParseCount(match.Groups["count"].Value);
                    if (count >= 0) stated.Add(new KeyValuePair<int, string>(count, sentence));
                }
            }

            int actual = (objective.AcceptanceCriteria ?? new List<string>()).Count(item => !String.IsNullOrWhiteSpace(item));
            bool disagree = stated.Select(pair => pair.Key).Distinct().Count() > 1
                || (stated.Count > 0 && actual > 0 && stated[0].Key != actual);

            List<string> sentences = new List<string>();
            if (!disagree) return sentences;
            foreach (KeyValuePair<int, string> pair in stated) AddBounded(sentences, pair.Value);
            return sentences;
        }

        #endregion

        #region Private-Methods

        private static IEnumerable<string> Sentences(Objective objective)
        {
            List<string> sources = new List<string>();
            if (!String.IsNullOrWhiteSpace(objective.Description)) sources.Add(objective.Description!);
            sources.AddRange(objective.AcceptanceCriteria ?? new List<string>());
            sources.AddRange(objective.NonGoals ?? new List<string>());
            sources.AddRange(objective.RolloutConstraints ?? new List<string>());
            if (!String.IsNullOrWhiteSpace(objective.RefinementSummary)) sources.Add(objective.RefinementSummary!);

            foreach (string source in sources)
            {
                if (String.IsNullOrWhiteSpace(source)) continue;
                foreach (string sentence in _SentenceBreak.Split(source))
                {
                    string trimmed = sentence.Trim();
                    if (trimmed.Length > 0) yield return trimmed;
                }
            }
        }

        private static bool SameObjective(string cited, string? stored)
        {
            if (String.IsNullOrWhiteSpace(stored)) return false;
            // Text often cites an objective by its short form, obj_ plus the first eight characters.
            const int shortLength = 12;
            if (cited.Length < shortLength || stored!.Length < shortLength) return String.Equals(cited, stored, StringComparison.Ordinal);
            return String.Equals(cited.Substring(0, shortLength), stored.Substring(0, shortLength), StringComparison.Ordinal);
        }

        private static int ParseCount(string value)
        {
            if (Int32.TryParse(value, out int number)) return number;
            return Array.IndexOf(_NumberWords, value.ToLowerInvariant());
        }

        private static void AddBounded(List<string> sentences, string sentence)
        {
            string quoted = sentence.Length > MaxSentenceLength ? sentence.Substring(0, MaxSentenceLength - 3) + "..." : sentence;
            if (sentences.Count < MaxSentences && !sentences.Contains(quoted)) sentences.Add(quoted);
        }

        #endregion
    }
}
