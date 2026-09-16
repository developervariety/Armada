namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Mines the search terms the prior-art retriever greps for: identifiers of five characters or more,
    /// type and method names, and file paths, drawn from an objective's text (and any extra text such as
    /// a captain's plan or a diff's added type names). The extraction is deterministic and bounded, so
    /// the retrieval it feeds is a unit-testable property of the input, not of a live tree.
    ///
    /// Type and method names and file paths are the strong signals — a novel class name in another
    /// branch is real prior art — so they are ordered first; plain long identifiers follow. Common
    /// English words of five or more letters are dropped: they match everywhere and turn a targeted
    /// grep into a full-text scan.
    /// </summary>
    public static class PriorArtIdentifierExtractor
    {
        #region Private-Members

        // A file path token: at least one slash, or a bare filename with a code/text extension.
        private static readonly Regex _FilePathPattern = new Regex(
            @"\b[A-Za-z0-9_][A-Za-z0-9_./-]*\.(?:cs|md|json|jsonc|ya?ml|xml|sql|txt|sh|py|ts|js|csproj|sln)\b",
            RegexOptions.Compiled);

        // A camel-case or Pascal-case identifier: a lower/upper run followed by at least one upper-then-
        // lower run, so it has an internal capital. These read as type or method names.
        private static readonly Regex _CasedIdentifierPattern = new Regex(
            @"\b[A-Za-z][a-z0-9]*(?:[A-Z][a-z0-9]+){1,}\b",
            RegexOptions.Compiled);

        // A backticked identifier the operator marked as code, honoured even when it is a single lower
        // word (for example a settings key). Kept because the operator explicitly flagged it.
        private static readonly Regex _BacktickPattern = new Regex(
            @"`([A-Za-z_][A-Za-z0-9_.]{2,})`",
            RegexOptions.Compiled);

        // A plain identifier of five or more word characters, the weakest signal.
        private static readonly Regex _LongWordPattern = new Regex(
            @"\b[A-Za-z_][A-Za-z0-9_]{4,}\b",
            RegexOptions.Compiled);

        // Five-or-more-letter English words that match everywhere; dropping them keeps the grep targeted.
        // Not exhaustive — a stopword that slips through only widens a search, never narrows it wrongly.
        private static readonly HashSet<string> _StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "about", "above", "after", "again", "against", "already", "always", "another", "because",
            "before", "being", "below", "between", "both", "cannot", "change", "changed", "changes",
            "check", "class", "could", "current", "delete", "described", "different", "docs", "document",
            "does", "doing", "during", "either", "empty", "every", "except", "exists", "extends", "false",
            "field", "first", "following", "given", "greater", "instead", "into", "landed", "later",
            "least", "leave", "line", "lines", "match", "matches", "method", "might", "missing", "model",
            "never", "objective", "other", "output", "over", "path", "point", "present", "proof", "prove",
            "reason", "record", "repository", "result", "return", "returns", "review", "rules", "same",
            "scope", "search", "seams", "shall", "should", "since", "source", "stage", "state", "still",
            "target", "terms", "test", "tests", "than", "that", "their", "them", "then", "there", "these",
            "thing", "those", "through", "under", "until", "using", "value", "verify", "vessel", "voyage",
            "where", "which", "while", "whole", "with", "within", "without", "words", "work", "would",
            "write", "written", "wrong"
        };

        private const int _MinLongWordLength = 5;
        private const int _MaxTerms = 24;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Extract the ordered, de-duplicated, bounded search-term set from the supplied text fragments.
        /// File paths and cased identifiers (and operator-backticked identifiers) come first; plain long
        /// identifiers follow. Comparison is ordinal and case-sensitive so <c>DecodeScaled</c> and
        /// <c>decodescaled</c> are two terms; duplicates by exact spelling are collapsed.
        /// </summary>
        /// <param name="fragments">The text fragments to mine (title, description, criteria, extra text).</param>
        /// <returns>Up to twenty-four terms, strong signals first.</returns>
        public static IReadOnlyList<string> Extract(params string?[] fragments)
        {
            List<string> strong = new List<string>();
            List<string> weak = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string? fragment in fragments ?? Array.Empty<string?>())
            {
                if (String.IsNullOrWhiteSpace(fragment)) continue;

                AddMatches(_FilePathPattern, fragment, group: 0, strong, seen, dropStopWords: false, minLength: 0);
                AddMatches(_BacktickPattern, fragment, group: 1, strong, seen, dropStopWords: false, minLength: 0);
                AddMatches(_CasedIdentifierPattern, fragment, group: 0, strong, seen, dropStopWords: true, minLength: 0);
                AddMatches(_LongWordPattern, fragment, group: 0, weak, seen, dropStopWords: true, minLength: _MinLongWordLength);
            }

            List<string> ordered = new List<string>(strong.Count + weak.Count);
            ordered.AddRange(strong);
            ordered.AddRange(weak);
            if (ordered.Count > _MaxTerms) ordered = ordered.GetRange(0, _MaxTerms);
            return ordered;
        }

        #endregion

        #region Private-Methods

        private static void AddMatches(
            Regex pattern,
            string fragment,
            int group,
            List<string> sink,
            HashSet<string> seen,
            bool dropStopWords,
            int minLength)
        {
            foreach (Match match in pattern.Matches(fragment))
            {
                if (!match.Success) continue;
                string term = (group == 0 ? match.Value : match.Groups[group].Value).Trim();
                if (term.Length == 0) continue;
                if (minLength > 0 && term.Length < minLength) continue;
                if (dropStopWords && _StopWords.Contains(term)) continue;
                if (!seen.Add(term)) continue;
                sink.Add(term);
            }
        }

        #endregion
    }
}
