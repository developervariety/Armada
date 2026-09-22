namespace Armada.Runtimes.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    /// <summary>
    /// The exact-text search and replacement both file-edit tools use, so a single edit and each step of a
    /// multi-edit obey one rule: the search text must be non-empty and must occur exactly once in the content it
    /// is applied to. The search observes cancellation between matches, counts line numbers in one pass, and
    /// lists at most <see cref="MaximumReportedCandidates"/> candidate lines however many matches exist.
    /// </summary>
    internal sealed class ExactTextMatch
    {
        #region Public-Members

        /// <summary>Most candidate line numbers an ambiguous match reports.</summary>
        public const int MaximumReportedCandidates = 20;

        /// <summary>Number of occurrences, overlapping occurrences included.</summary>
        public int MatchCount { get; private set; } = 0;

        /// <summary>Position of the first occurrence, or -1 when there is none.</summary>
        public int FirstPosition { get; private set; } = -1;

        /// <summary>One-based line numbers of the first occurrences, at most <see cref="MaximumReportedCandidates"/>.</summary>
        public List<int> CandidateLineNumbers { get; } = new List<int>();

        /// <summary>Whether exactly one occurrence exists.</summary>
        public bool IsUnique => MatchCount == 1;

        #endregion

        #region Constructors-and-Factories

        private ExactTextMatch()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Find every occurrence of a non-empty search text.
        /// </summary>
        /// <param name="content">Content to search, with LF line endings.</param>
        /// <param name="search">Search text, with LF line endings; must not be empty.</param>
        /// <param name="token">Cancellation token, observed between matches.</param>
        /// <returns>The occurrences found.</returns>
        /// <exception cref="ArgumentException">The search text is empty.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
        public static ExactTextMatch Find(string content, string search, CancellationToken token)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (String.IsNullOrEmpty(search)) throw new ArgumentException("The search text must not be empty.", nameof(search));

            ExactTextMatch match = new ExactTextMatch();
            int lineNumber = 1;
            int linesCountedTo = 0;
            int index = 0;
            while (index <= content.Length - search.Length)
            {
                token.ThrowIfCancellationRequested();
                int found = content.IndexOf(search, index, StringComparison.Ordinal);
                if (found < 0) break;

                match.MatchCount++;
                if (match.FirstPosition < 0) match.FirstPosition = found;
                if (match.CandidateLineNumbers.Count < MaximumReportedCandidates)
                {
                    lineNumber += content.AsSpan(linesCountedTo, found - linesCountedTo).Count('\n');
                    linesCountedTo = found;
                    match.CandidateLineNumbers.Add(lineNumber);
                }

                index = found + 1;
            }

            return match;
        }

        /// <summary>
        /// Replace the search text at one position.
        /// </summary>
        /// <param name="content">Content to edit.</param>
        /// <param name="position">Position of the occurrence.</param>
        /// <param name="searchLength">Length of the search text.</param>
        /// <param name="replacement">Replacement text.</param>
        /// <returns>The edited content.</returns>
        public static string Replace(string content, int position, int searchLength, string replacement)
        {
            return String.Concat(content.AsSpan(0, position), replacement, content.AsSpan(position + searchLength));
        }

        /// <summary>
        /// Convert CRLF and lone CR line endings to LF.
        /// </summary>
        /// <param name="text">Text to normalize.</param>
        /// <returns>Text with LF line endings.</returns>
        public static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        #endregion
    }
}
