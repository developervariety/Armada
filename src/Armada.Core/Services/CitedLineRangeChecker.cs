namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// Finds the file line ranges that mission or objective text cites and checks each against the file
    /// as a revision holds it. Text ages: a range drifts past the end of a file that shrank, or away from
    /// the symbol it named once code moved, and a captain then re-measures every citation.
    ///
    /// A citation names a path from the repository root, a bare file name (<c>Foo.cs:12</c>), or a path in a
    /// sibling checkout (<c>../Sibling/src/Foo.cs:12</c>). It is paired with a symbol when the text writes
    /// <c>`Symbol` (`path:line`)</c>, <c>`Symbol` (path:line)</c> or <c>`Symbol` at `path:line`</c>; failing
    /// that, when the sentence holding the citation cites nothing else and names exactly one backticked
    /// identifier or call expression, before or after it. The symbol must then appear inside the cited lines.
    /// </summary>
    public static class CitedLineRangeChecker
    {
        #region Public-Members

        /// <summary>
        /// The most citations read from one text.
        /// </summary>
        public const int MaxCitations = 12;

        #endregion

        #region Private-Members

        // The path may not continue a word, a host name, a URL or a longer path, so `host.example.com:8443`
        // inside a URL and the tail of `a/b.cs` are never read as citations of their own.
        private static readonly Regex _Citation = new Regex(
            @"(?:`(?<symbol>[A-Za-z_][A-Za-z0-9_.]*)`\s*(?:\(\s*|at\s+))?`?(?<![A-Za-z0-9_./:@\-])(?<path>(?:\.{1,2}/)*[A-Za-z0-9_][A-Za-z0-9_./-]*\.[A-Za-z][A-Za-z0-9]*):(?<start>\d{1,7})(?:-(?<end>\d{1,7}))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _BacktickSpan = new Regex(
            @"`(?<content>[^`\r\n]{1,200})`",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _SymbolSpan = new Regex(
            @"^(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)(?<call>\([^()]*\))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the cited line ranges from text, in order, without duplicates.
        /// </summary>
        /// <param name="text">Mission or objective text.</param>
        /// <returns>The citations; empty when the text cites none.</returns>
        public static List<CitedLineRange> Parse(string? text)
        {
            List<CitedLineRange> results = new List<CitedLineRange>();
            if (String.IsNullOrWhiteSpace(text)) return results;
            MatchCollection matches = _Citation.Matches(text);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in matches)
            {
                int start = Int32.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
                int end = match.Groups["end"].Success ? Int32.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture) : start;
                if (start < 1 || end < start) continue;
                CitedLineRange range = new CitedLineRange
                {
                    Path = match.Groups["path"].Value,
                    StartLine = start,
                    EndLine = end,
                    Symbol = match.Groups["symbol"].Success ? match.Groups["symbol"].Value : InferSymbol(text, match, matches)
                };
                if (!seen.Add(range.Text + "|" + range.Symbol)) continue;
                results.Add(range);
                if (results.Count >= MaxCitations) break;
            }
            return results;
        }

        /// <summary>
        /// Check one citation against the lines of its file.
        /// </summary>
        /// <param name="range">The citation.</param>
        /// <param name="lines">The file's lines on the revision.</param>
        /// <returns>A one-sentence issue naming the citation, or null when the citation holds.</returns>
        public static string? Evaluate(CitedLineRange range, IReadOnlyList<string> lines)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            if (lines == null) throw new ArgumentNullException(nameof(lines));

            if (range.StartLine > lines.Count)
                return "`" + range.Text + "` is past the end of the file (" + lines.Count + " lines).";
            if (String.IsNullOrEmpty(range.Symbol)) return null;

            // A qualified name is cited by its member: a declaration line holds only the last segment, and a
            // call site holds the whole name. The member is matched as a whole word, so a short name such as
            // `Min` is not found inside `MinValue`.
            string symbol = range.Symbol!;
            int dot = symbol.LastIndexOf('.');
            string member = dot >= 0 && dot < symbol.Length - 1 ? symbol.Substring(dot + 1) : symbol;

            int last = Math.Min(range.EndLine, lines.Count);
            for (int line = range.StartLine; line <= last; line++)
            {
                if (ContainsWord(lines[line - 1], member)) return null;
            }

            int current = dot >= 0 ? FindLine(lines, symbol) : 0;
            if (current == 0) current = FindLine(lines, member);
            if (current > 0)
                return "`" + symbol + "` is not within `" + range.Text + "`; it is at line " + current + ".";
            return "`" + symbol + "` is not within `" + range.Text + "` and does not appear in the file.";
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// The symbol a citation's sentence names when the text does not pair one with it directly. Only a
        /// sentence that holds this one citation and exactly one backticked identifier or call expression
        /// names a symbol; anything less certain names none, so a citation is never checked against a word
        /// the text did not tie to it.
        /// </summary>
        private static string? InferSymbol(string text, Match citation, MatchCollection citations)
        {
            int lineStart = citation.Index == 0 ? 0 : text.LastIndexOf('\n', citation.Index - 1) + 1;
            int lineEnd = text.IndexOf('\n', citation.Index + citation.Length);
            if (lineEnd < 0) lineEnd = text.Length;

            int sentenceStart = lineStart;
            for (int index = citation.Index - 1; index >= lineStart; index--)
            {
                if (IsSentenceEnd(text, index))
                {
                    sentenceStart = index + 1;
                    break;
                }
            }

            int sentenceEnd = lineEnd;
            for (int index = citation.Index + citation.Length; index < lineEnd; index++)
            {
                if (IsSentenceEnd(text, index))
                {
                    sentenceEnd = index;
                    break;
                }
            }

            foreach (Match other in citations)
            {
                if (other.Index != citation.Index && other.Index >= sentenceStart && other.Index < sentenceEnd) return null;
            }

            string fileName = citation.Groups["path"].Value;
            int slash = fileName.LastIndexOf('/');
            if (slash >= 0) fileName = fileName.Substring(slash + 1);

            string? found = null;
            string window = text.Substring(sentenceStart, sentenceEnd - sentenceStart);
            foreach (Match span in _BacktickSpan.Matches(window))
            {
                int spanStart = sentenceStart + span.Index;
                int spanEnd = spanStart + span.Length;
                if (spanEnd > citation.Index && spanStart < citation.Index + citation.Length) continue;
                string? candidate = SymbolOf(span.Groups["content"].Value, fileName);
                if (candidate == null) continue;
                if (found != null && !String.Equals(found, candidate, StringComparison.Ordinal)) return null;
                found = candidate;
            }
            return found;
        }

        /// <summary>
        /// The symbol a backticked span names, or null when the span reads as a file name, a plain word, or
        /// anything other than an identifier or call expression.
        /// </summary>
        private static string? SymbolOf(string content, string citedFileName)
        {
            Match symbol = _SymbolSpan.Match(content.Trim());
            if (!symbol.Success) return null;
            string name = symbol.Groups["name"].Value;
            if (symbol.Groups["call"].Success) return name;
            if (String.Equals(name, citedFileName, StringComparison.OrdinalIgnoreCase)) return null;

            int dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                // `Foo.cs` or `settings.json` is a file name: a short lower-case last segment is an extension.
                string tail = name.Substring(dot + 1);
                if (tail.Length <= 4 && String.Equals(tail, tail.ToLowerInvariant(), StringComparison.Ordinal)) return null;
                return name;
            }

            // A lone lower-case word in backticks is as often a value or a term as a symbol.
            foreach (char c in name)
            {
                if (Char.IsUpper(c) || c == '_') return name;
            }
            return null;
        }

        private static bool IsSentenceEnd(string text, int index)
        {
            char c = text[index];
            if (c != '.' && c != '?' && c != '!' && c != ';') return false;
            return index + 1 >= text.Length || Char.IsWhiteSpace(text[index + 1]);
        }

        private static int FindLine(IReadOnlyList<string> lines, string word)
        {
            for (int line = 1; line <= lines.Count; line++)
            {
                if (ContainsWord(lines[line - 1], word)) return line;
            }
            return 0;
        }

        private static bool ContainsWord(string line, string word)
        {
            if (String.IsNullOrEmpty(line) || String.IsNullOrEmpty(word)) return false;
            int index = line.IndexOf(word, StringComparison.Ordinal);
            while (index >= 0)
            {
                bool startsWord = index == 0 || !IsWordChar(line[index - 1]);
                int after = index + word.Length;
                bool endsWord = after >= line.Length || !IsWordChar(line[after]);
                if (startsWord && endsWord) return true;
                index = line.IndexOf(word, index + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool IsWordChar(char c)
        {
            return Char.IsLetterOrDigit(c) || c == '_';
        }

        #endregion
    }
}
