namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
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
    ///
    /// A range that falls inside the span of one declaration of the symbol (from its declaration line to the
    /// brace that closes its body, or to the end of its indented block) holds: it cites lines of that symbol.
    ///
    /// A symbol that has moved is reported where it is declared: the declaration of the member nearest the
    /// cited lines, then any other occurrence nearest them. A call site earlier in the file is never named as
    /// the symbol's place.
    /// </summary>
    public static class CitedLineRangeChecker
    {
        #region Public-Members

        /// <summary>
        /// The most citations read from one text.
        /// </summary>
        public const int MaxCitations = 12;

        /// <summary>
        /// The most lines a moved symbol may sit from its cited range and still count as drift: the citation
        /// names the right code, and only its line numbers are stale.
        /// </summary>
        public const int DriftToleranceLines = 10;

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

        private static readonly char[] _NotDeclarationPrefix = new[] { '(', '=', '"', '\'', '/', '#' };

        private static readonly Regex _TrailingWord = new Regex(@"[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Words that stand before a USE of a name, never before its declaration.
        private static readonly HashSet<string> _UseKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "new", "await", "throw", "yield", "case", "goto", "nameof", "typeof", "sizeof",
            "is", "as", "in", "else", "and", "or", "not", "using", "import", "from", "assert", "raise", "del"
        };

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
            return Evaluate(range, lines, out int _);
        }

        /// <summary>
        /// Check one citation against the lines of its file, and say where a moved symbol is now.
        /// </summary>
        /// <param name="range">The citation.</param>
        /// <param name="lines">The file's lines on the revision.</param>
        /// <param name="currentLine">The line the cited symbol is at now when it has moved out of the range; 0
        /// when the citation holds, runs past the end of the file, or names a symbol the file does not hold.</param>
        /// <returns>A one-sentence issue naming the citation, or null when the citation holds.</returns>
        public static string? Evaluate(CitedLineRange range, IReadOnlyList<string> lines, out int currentLine)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            currentLine = 0;

            if (GitLfsPointer.IsPointer(lines)) return LfsNotVerifiedIssue(range);
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

            // A range inside the body of the symbol's declaration cites that symbol: a reader names a member and
            // points at the lines of it that matter.
            if (EnclosingDeclaration(lines, range, member) > 0) return null;

            int declaration = NearestLine(lines, range.StartLine, line => IsDeclarationOf(line, member));
            if (declaration > 0)
            {
                currentLine = declaration;
                return "`" + symbol + "` is not within `" + range.Text + "`; it is declared at line " + declaration + ".";
            }

            int current = dot >= 0 ? NearestLine(lines, range.StartLine, line => ContainsWord(line, symbol)) : 0;
            if (current == 0) current = NearestLine(lines, range.StartLine, line => ContainsWord(line, member));
            if (current > 0)
            {
                currentLine = current;
                return "`" + symbol + "` is not within `" + range.Text + "`; it is at line " + current + ".";
            }
            return "`" + symbol + "` is not within `" + range.Text + "` and does not appear in the file.";
        }

        /// <summary>
        /// Whether a moved symbol sits within <see cref="DriftToleranceLines"/> of its cited range, so only the
        /// citation's line numbers are stale.
        /// </summary>
        /// <param name="range">The citation.</param>
        /// <param name="currentLine">The line <see cref="Evaluate(CitedLineRange, IReadOnlyList{string}, out int)"/> reported.</param>
        /// <returns>True when the symbol moved by no more than the tolerance.</returns>
        public static bool IsSmallDrift(CitedLineRange range, int currentLine)
        {
            if (range == null || currentLine < 1) return false;
            int distance = currentLine < range.StartLine ? range.StartLine - currentLine : currentLine - range.EndLine;
            return distance <= DriftToleranceLines;
        }

        /// <summary>
        /// The issue for a citation of a file stored through Git LFS whose content could not be read here: the
        /// lines read are the LFS pointer, which says nothing about the file's lines, so the citation is named
        /// as not checked, never as past the end of the file.
        /// </summary>
        /// <param name="range">The citation.</param>
        /// <returns>The one-sentence issue.</returns>
        public static string LfsNotVerifiedIssue(CitedLineRange range)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            return "`" + range.Text + "` is in a file stored through Git LFS whose content is not available here, so it was not checked.";
        }

        /// <summary>
        /// Whether a citation holds in a file: its first line is in the file and its symbol, if any, is within
        /// the cited lines, around them, or moved by no more than <see cref="DriftToleranceLines"/>. A Git LFS
        /// pointer read in place of a file's content cannot disprove a citation, so it holds.
        /// </summary>
        /// <param name="range">The citation.</param>
        /// <param name="lines">The file's lines.</param>
        /// <returns>True when the citation names this file's code.</returns>
        public static bool Holds(CitedLineRange range, IReadOnlyList<string> lines)
        {
            if (GitLfsPointer.IsPointer(lines)) return true;
            string? issue = Evaluate(range, lines, out int currentLine);
            return issue == null || IsSmallDrift(range, currentLine);
        }

        /// <summary>
        /// Of the files a cited name matches, the ones whose lines hold every given citation, in candidate
        /// order. A bare file name that matches several files means the one whose lines hold the citation:
        /// exactly one holding candidate resolves the name, several leave it ambiguous, and none make the
        /// citation stale. Every caller that resolves a cited name against several files asks this rule.
        /// </summary>
        /// <param name="ranges">The citations of the name.</param>
        /// <param name="candidates">The files the name matches.</param>
        /// <param name="readLines">Reads a candidate's lines; null when it cannot be read.</param>
        /// <returns>The holding candidates; an unreadable candidate never holds.</returns>
        public static async Task<List<string>> CandidatesHoldingAsync(
            IReadOnlyList<CitedLineRange> ranges,
            IEnumerable<string> candidates,
            Func<string, Task<IReadOnlyList<string>?>> readLines)
        {
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (readLines == null) throw new ArgumentNullException(nameof(readLines));
            List<string> holding = new List<string>();
            foreach (string candidate in candidates)
            {
                IReadOnlyList<string>? lines = await readLines(candidate).ConfigureAwait(false);
                if (lines == null) continue;
                bool holds = true;
                foreach (CitedLineRange range in ranges)
                {
                    if (Holds(range, lines)) continue;
                    holds = false;
                    break;
                }
                if (holds) holding.Add(candidate);
            }
            return holding;
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

        /// <summary>
        /// The line of a declaration of the member whose span holds the whole cited range, or 0 when none does.
        /// The span runs from the declaration line to its closing brace, or, for a declaration line that ends with
        /// a colon and opens no brace, to the end of its indented block.
        /// </summary>
        private static int EnclosingDeclaration(IReadOnlyList<string> lines, CitedLineRange range, string member)
        {
            int last = Math.Min(range.EndLine, lines.Count);
            for (int line = range.StartLine; line >= 1; line--)
            {
                if (!IsDeclarationOf(lines[line - 1], member)) continue;
                int end = DeclarationSpanEnd(lines, line);
                if (end >= last) return line;
            }
            return 0;
        }

        /// <summary>
        /// The last line of the declaration that starts at a line: the line of the brace that closes its body, the
        /// line that ends a body-less declaration with a semicolon, or the end of an indented block. Braces inside
        /// strings, character literals and comments do not count. Returns the declaration line itself when no body
        /// is found.
        /// </summary>
        private static int DeclarationSpanEnd(IReadOnlyList<string> lines, int declarationLine)
        {
            int depth = 0;
            bool opened = false;
            bool blockComment = false;
            for (int line = declarationLine; line <= lines.Count; line++)
            {
                string text = lines[line - 1] ?? String.Empty;
                char quote = '\0';
                bool verbatim = false;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    char next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (blockComment)
                    {
                        if (c == '*' && next == '/') { blockComment = false; i++; }
                        continue;
                    }
                    if (quote != '\0')
                    {
                        if (verbatim)
                        {
                            if (c == '"' && next == '"') { i++; continue; }
                            if (c == '"') quote = '\0';
                            continue;
                        }
                        if (c == '\\') { i++; continue; }
                        if (c == quote) quote = '\0';
                        continue;
                    }
                    if (c == '/' && next == '/') break;
                    if (c == '/' && next == '*') { blockComment = true; i++; continue; }
                    if (c == '#' && !opened && line == declarationLine && text.Substring(0, i).Trim().Length > 0) break;
                    if (c == '"' || c == '\'' || c == '`')
                    {
                        quote = c;
                        verbatim = c == '"' && i > 0 && text[i - 1] == '@';
                        continue;
                    }
                    if (c == '{') { depth++; opened = true; continue; }
                    if (c == '}')
                    {
                        depth--;
                        if (opened && depth <= 0) return line;
                        continue;
                    }
                    if (c == ';' && !opened) return line;
                }
                if (!opened && line == declarationLine && EndsWithColon(text))
                    return IndentedBlockEnd(lines, declarationLine);
            }
            return opened ? lines.Count : declarationLine;
        }

        private static bool EndsWithColon(string text)
        {
            int hash = text.IndexOf('#');
            string code = (hash >= 0 ? text.Substring(0, hash) : text).TrimEnd();
            return code.EndsWith(":", StringComparison.Ordinal);
        }

        // The last non-blank line indented deeper than the declaration line.
        private static int IndentedBlockEnd(IReadOnlyList<string> lines, int declarationLine)
        {
            int indent = Indent(lines[declarationLine - 1]);
            int end = declarationLine;
            for (int line = declarationLine + 1; line <= lines.Count; line++)
            {
                string text = lines[line - 1] ?? String.Empty;
                if (text.Trim().Length == 0) continue;
                if (Indent(text) <= indent) break;
                end = line;
            }
            return end;
        }

        private static int Indent(string? text)
        {
            int count = 0;
            foreach (char c in text ?? String.Empty)
            {
                if (c == ' ') count++;
                else if (c == '\t') count += 4;
                else break;
            }
            return count;
        }

        // The matching line nearest the cited start; the earlier line wins a tie.
        private static int NearestLine(IReadOnlyList<string> lines, int citedStart, Func<string, bool> matches)
        {
            int best = 0;
            int bestDistance = Int32.MaxValue;
            for (int line = 1; line <= lines.Count; line++)
            {
                if (!matches(lines[line - 1])) continue;
                int distance = Math.Abs(line - citedStart);
                if (distance < bestDistance)
                {
                    best = line;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>
        /// Whether a line declares the member rather than using it: a type, modifier or keyword stands before the
        /// name, nothing before it opens a call, an assignment or a string, and the name is followed by a
        /// parameter list, a type argument list, a body, a base list, an initializer, the end of a field, or
        /// the end of the line. A call, a member access, a constructor call and a comment are not declarations.
        /// </summary>
        private static bool IsDeclarationOf(string line, string member)
        {
            if (String.IsNullOrEmpty(line) || String.IsNullOrEmpty(member)) return false;
            int index = line.IndexOf(member, StringComparison.Ordinal);
            while (index >= 0)
            {
                int after = index + member.Length;
                bool startsWord = index == 0 || (!IsWordChar(line[index - 1]) && line[index - 1] != '.');
                bool endsWord = after >= line.Length || !IsWordChar(line[after]);
                if (startsWord && endsWord && DeclaresAt(line, index, after)) return true;
                index = line.IndexOf(member, index + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool DeclaresAt(string line, int index, int after)
        {
            string prefix = line.Substring(0, index);
            if (prefix.IndexOfAny(_NotDeclarationPrefix) >= 0) return false;
            string trimmed = prefix.TrimEnd();
            if (trimmed.Length == 0 || trimmed.Length == prefix.Length) return false;
            char last = trimmed[trimmed.Length - 1];
            if (!IsWordChar(last) && last != '>' && last != ']' && last != '?' && last != '*' && last != '&') return false;
            Match word = _TrailingWord.Match(trimmed);
            if (word.Success && _UseKeywords.Contains(word.Value)) return false;

            int next = after;
            while (next < line.Length && Char.IsWhiteSpace(line[next])) next++;
            if (next >= line.Length) return true;
            char c = line[next];
            return c == '(' || c == '<' || c == '{' || c == ':' || c == '=' || c == ';';
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
