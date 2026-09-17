namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Splits source files into index chunks.
    ///
    /// Line windows cut a file every N lines, so the same method falls into different chunks
    /// depending on where it sits in its file, and two copies of it never produce the same chunk.
    /// For brace-delimited languages this chunker follows the block structure instead: a block that
    /// fits the line budget is one chunk, and a block that does not is split at its child blocks, so
    /// a method becomes one chunk together with its doc comment, attributes and signature. The lines
    /// between members (a type header, fields) become their own piece. Pieces with fewer non-blank lines
    /// than a minimum (properties, one-line members) are packed with their small neighbours up to the
    /// line budget, so the chunk count stays close to line windows. Lines holding only braces,
    /// parentheses or semicolons are not emitted on their own.
    ///
    /// The scanner skips comments, strings, character literals, C# verbatim, raw and interpolated
    /// strings, and JavaScript template literals. When the braces of a file do not balance, the file
    /// falls back to line windows, which are always correct if coarser.
    /// </summary>
    public static class StructuralChunker
    {
        #region Private-Members

        private static readonly HashSet<string> _BraceLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csharp", "java", "javascript", "typescript", "kotlin", "rust", "go",
            "c", "h", "cpp", "hpp", "cc", "swift", "scala", "dart", "php"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a language is chunked structurally.
        /// </summary>
        /// <param name="language">Language name as the code index detects it.</param>
        /// <returns>True when the language uses braces for blocks.</returns>
        public static bool SupportsLanguage(string? language)
        {
            return !String.IsNullOrWhiteSpace(language) && _BraceLanguages.Contains(language);
        }

        /// <summary>
        /// Split a file into fixed line windows. Windows holding only whitespace are skipped.
        /// </summary>
        /// <param name="lines">File lines.</param>
        /// <param name="maxLines">Lines per window.</param>
        /// <returns>Chunk ranges in file order.</returns>
        public static List<CodeChunkRange> ChunkByLineWindows(string[] lines, int maxLines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (maxLines < 1) throw new ArgumentOutOfRangeException(nameof(maxLines));

            List<CodeChunkRange> ranges = new List<CodeChunkRange>();
            AddLineWindows(lines, 0, lines.Length, maxLines, ranges, trimBlankEdges: false);
            return ranges;
        }

        /// <summary>
        /// Split a file into chunks at declaration boundaries when the language supports it and the
        /// braces balance, otherwise into fixed line windows.
        /// </summary>
        /// <param name="lines">File lines.</param>
        /// <param name="language">Language name as the code index detects it.</param>
        /// <param name="maxLines">Maximum lines per chunk.</param>
        /// <param name="minStandaloneLines">Non-blank lines a member needs to be a chunk of its own.
        /// Smaller neighbouring members (properties, one-line methods, field groups) are packed together
        /// up to <paramref name="maxLines"/>, which keeps the chunk count close to line windows.</param>
        /// <returns>Chunk ranges in file order.</returns>
        public static List<CodeChunkRange> Chunk(string[] lines, string? language, int maxLines, int minStandaloneLines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (maxLines < 1) throw new ArgumentOutOfRangeException(nameof(maxLines));
            if (minStandaloneLines < 1) throw new ArgumentOutOfRangeException(nameof(minStandaloneLines));

            if (!SupportsLanguage(language)) return ChunkByLineWindows(lines, maxLines);

            BraceBlock? root = ParseBlocks(lines, language!);
            if (root == null) return ChunkByLineWindows(lines, maxLines);

            ChunkBuilder builder = new ChunkBuilder(lines, maxLines, minStandaloneLines);
            ChunkRange(builder, 0, lines.Length, root.Children);
            builder.Flush();
            return builder.Ranges;
        }

        #endregion

        #region Private-Methods

        private static void ChunkRange(ChunkBuilder builder, int start, int endExclusive, List<BraceBlock> children)
        {
            string[] lines = builder.Lines;
            int maxLines = builder.MaxLines;

            if (endExclusive - start <= maxLines)
            {
                builder.AddPiece(start, endExclusive);
                return;
            }

            if (children.Count == 0)
            {
                builder.AddWindows(start, endExclusive);
                return;
            }

            int cursor = start;
            foreach (BraceBlock child in children)
            {
                int childEnd = child.CloseLine + 1;
                if (childEnd <= cursor) continue;

                int childStart = Math.Max(cursor, FindPreludeStart(lines, cursor, child.OpenLine));
                if (childStart > cursor) AddGap(builder, cursor, childStart);

                if (childEnd - childStart <= maxLines) builder.AddPiece(childStart, childEnd);
                else ChunkRange(builder, childStart, childEnd, child.Children);

                cursor = childEnd;
            }

            if (cursor < endExclusive) AddGap(builder, cursor, endExclusive);
        }

        /// <summary>
        /// Walk upward from a block's opening line to take in its signature, attributes and doc
        /// comment. Stops at a blank line or a line that ends a statement or another block.
        /// </summary>
        private static int FindPreludeStart(string[] lines, int floor, int openLine)
        {
            int start = openLine;
            int index = openLine - 1;
            while (index >= floor)
            {
                string trimmed = lines[index].Trim();
                if (trimmed.Length == 0) break;
                char last = trimmed[trimmed.Length - 1];
                if (last == ';' || last == '{' || last == '}') break;
                start = index;
                index--;
            }

            return start;
        }

        private static void AddGap(ChunkBuilder builder, int start, int endExclusive)
        {
            if (!HasMeaningfulLine(builder.Lines, start, endExclusive)) return;
            if (endExclusive - start <= builder.MaxLines) builder.AddPiece(start, endExclusive);
            else builder.AddWindows(start, endExclusive);
        }

        private static void TrimBlankEdges(string[] lines, ref int start, ref int endExclusive)
        {
            while (start < endExclusive && lines[start].Trim().Length == 0) start++;
            while (endExclusive > start && lines[endExclusive - 1].Trim().Length == 0) endExclusive--;
        }

        private static void AddLineWindows(
            string[] lines,
            int start,
            int endExclusive,
            int maxLines,
            List<CodeChunkRange> ranges,
            bool trimBlankEdges)
        {
            for (int windowStart = start; windowStart < endExclusive; windowStart += maxLines)
            {
                int windowEnd = Math.Min(endExclusive, windowStart + maxLines);
                bool hasContent = false;
                for (int i = windowStart; i < windowEnd; i++)
                {
                    if (lines[i].Trim().Length > 0)
                    {
                        hasContent = true;
                        break;
                    }
                }

                if (!hasContent) continue;
                int rangeStart = windowStart;
                int rangeEnd = windowEnd;
                if (trimBlankEdges) TrimBlankEdges(lines, ref rangeStart, ref rangeEnd);
                ranges.Add(new CodeChunkRange(rangeStart, rangeEnd));
            }
        }

        private static bool HasMeaningfulLine(string[] lines, int start, int endExclusive)
        {
            for (int i = start; i < endExclusive; i++)
            {
                foreach (char c in lines[i])
                {
                    if (Char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '(' || c == ')' || c == ';' || c == ',') continue;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Build the brace block tree of a file. Returns null when the braces do not balance.
        /// </summary>
        private static BraceBlock? ParseBlocks(string[] lines, string language)
        {
            bool isCSharp = String.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase);
            bool isScript = String.Equals(language, "javascript", StringComparison.OrdinalIgnoreCase)
                || String.Equals(language, "typescript", StringComparison.OrdinalIgnoreCase);
            bool isGo = String.Equals(language, "go", StringComparison.OrdinalIgnoreCase);

            BraceBlock root = new BraceBlock(-1);
            Stack<BraceBlock> open = new Stack<BraceBlock>();
            open.Push(root);

            // Each frame is a lexical mode. Code frames count braces; string frames with interpolation
            // holes push a Code frame for each hole and pop back when the hole's brace closes.
            Stack<ScanFrame> frames = new Stack<ScanFrame>();
            frames.Push(new ScanFrame(ScanModeEnum.Code));
            bool inBlockComment = false;

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                int i = 0;
                while (i < line.Length)
                {
                    char c = line[i];
                    char next = i + 1 < line.Length ? line[i + 1] : '\0';
                    ScanFrame frame = frames.Peek();

                    if (inBlockComment)
                    {
                        if (c == '*' && next == '/')
                        {
                            inBlockComment = false;
                            i += 2;
                        }
                        else
                        {
                            i++;
                        }

                        continue;
                    }

                    if (frame.Mode == ScanModeEnum.RawString)
                    {
                        if (c == '"' && CountRun(line, i, '"') >= frame.QuoteCount)
                        {
                            frames.Pop();
                            i += frame.QuoteCount;
                        }
                        else
                        {
                            i++;
                        }

                        continue;
                    }

                    if (frame.Mode == ScanModeEnum.InterpolatedString || frame.Mode == ScanModeEnum.VerbatimInterpolatedString)
                    {
                        bool verbatim = frame.Mode == ScanModeEnum.VerbatimInterpolatedString;
                        if (!verbatim && c == '\\') { i += 2; continue; }
                        if (c == '"')
                        {
                            if (verbatim && next == '"') { i += 2; continue; }
                            frames.Pop();
                            i++;
                            continue;
                        }

                        if (c == '{')
                        {
                            if (next == '{') { i += 2; continue; }
                            frames.Push(new ScanFrame(ScanModeEnum.Code) { IsHole = true });
                            i++;
                            continue;
                        }

                        i++;
                        continue;
                    }

                    if (frame.Mode == ScanModeEnum.TemplateLiteral)
                    {
                        if (c == '\\') { i += 2; continue; }
                        if (c == '`') { frames.Pop(); i++; continue; }
                        if (c == '$' && next == '{')
                        {
                            frames.Push(new ScanFrame(ScanModeEnum.Code) { IsHole = true });
                            i += 2;
                            continue;
                        }

                        i++;
                        continue;
                    }

                    if (frame.Mode == ScanModeEnum.MultiLineString)
                    {
                        // Go raw string: backtick-delimited, no escapes.
                        if (c == '`') frames.Pop();
                        i++;
                        continue;
                    }

                    // Code mode.
                    if (c == '/' && next == '/') break;
                    if (c == '/' && next == '*')
                    {
                        inBlockComment = true;
                        i += 2;
                        continue;
                    }

                    if (isCSharp && (c == '$' || c == '@'))
                    {
                        int prefixEnd = i;
                        bool interpolated = false;
                        bool verbatim = false;
                        while (prefixEnd < line.Length && (line[prefixEnd] == '$' || line[prefixEnd] == '@'))
                        {
                            if (line[prefixEnd] == '$') interpolated = true;
                            else verbatim = true;
                            prefixEnd++;
                        }

                        if (prefixEnd < line.Length && line[prefixEnd] == '"')
                        {
                            int quotes = CountRun(line, prefixEnd, '"');
                            if (quotes >= 3)
                            {
                                frames.Push(new ScanFrame(ScanModeEnum.RawString) { QuoteCount = quotes });
                                i = prefixEnd + quotes;
                                continue;
                            }

                            if (interpolated)
                            {
                                frames.Push(new ScanFrame(verbatim ? ScanModeEnum.VerbatimInterpolatedString : ScanModeEnum.InterpolatedString));
                                i = prefixEnd + 1;
                                continue;
                            }

                            i = SkipVerbatimString(lines, ref lineIndex, prefixEnd + 1, out line);
                            continue;
                        }

                        i++;
                        continue;
                    }

                    if (c == '"')
                    {
                        int quotes = CountRun(line, i, '"');
                        if (isCSharp && quotes >= 3)
                        {
                            frames.Push(new ScanFrame(ScanModeEnum.RawString) { QuoteCount = quotes });
                            i += quotes;
                            continue;
                        }

                        i = SkipQuoted(line, i + 1, '"');
                        continue;
                    }

                    if (c == '\'')
                    {
                        if (isScript)
                        {
                            i = SkipQuoted(line, i + 1, '\'');
                            continue;
                        }

                        // A character literal is 'x' or '\x...'; anything else (a Rust lifetime, a
                        // generic marker) is an ordinary character.
                        int close = FindCharLiteralEnd(line, i);
                        i = close > i ? close + 1 : i + 1;
                        continue;
                    }

                    if (c == '`')
                    {
                        if (isScript)
                        {
                            frames.Push(new ScanFrame(ScanModeEnum.TemplateLiteral));
                            i++;
                            continue;
                        }

                        if (isGo)
                        {
                            frames.Push(new ScanFrame(ScanModeEnum.MultiLineString));
                            i++;
                            continue;
                        }
                    }

                    if (c == '{')
                    {
                        frame.Depth++;
                        BraceBlock block = new BraceBlock(lineIndex);
                        open.Peek().Children.Add(block);
                        open.Push(block);
                        i++;
                        continue;
                    }

                    if (c == '}')
                    {
                        if (frame.IsHole && frame.Depth == 0)
                        {
                            frames.Pop();
                            i++;
                            continue;
                        }

                        if (open.Count <= 1) return null;
                        frame.Depth--;
                        BraceBlock closed = open.Pop();
                        closed.CloseLine = lineIndex;
                        i++;
                        continue;
                    }

                    i++;
                }
            }

            if (inBlockComment || open.Count != 1 || frames.Count != 1) return null;
            return root;
        }

        private static int CountRun(string line, int index, char value)
        {
            int count = 0;
            while (index + count < line.Length && line[index + count] == value) count++;
            return count;
        }

        /// <summary>Skip a single-line quoted string with backslash escapes. Returns the index after the closing quote.</summary>
        private static int SkipQuoted(string line, int index, char quote)
        {
            while (index < line.Length)
            {
                if (line[index] == '\\') { index += 2; continue; }
                if (line[index] == quote) return index + 1;
                index++;
            }

            return line.Length;
        }

        /// <summary>
        /// Skip a C# verbatim string, which may span lines and doubles a quote to escape it. Moves the
        /// line cursor when the string continues and returns the index after the closing quote.
        /// </summary>
        private static int SkipVerbatimString(string[] lines, ref int lineIndex, int index, out string line)
        {
            line = lines[lineIndex];
            while (true)
            {
                while (index < line.Length)
                {
                    if (line[index] == '"')
                    {
                        if (index + 1 < line.Length && line[index + 1] == '"') { index += 2; continue; }
                        return index + 1;
                    }

                    index++;
                }

                if (lineIndex + 1 >= lines.Length) return line.Length;
                lineIndex++;
                line = lines[lineIndex];
                index = 0;
            }
        }

        private static int FindCharLiteralEnd(string line, int openIndex)
        {
            int index = openIndex + 1;
            if (index >= line.Length) return -1;
            if (line[index] == '\\')
            {
                int limit = Math.Min(line.Length, openIndex + 12);
                for (int j = index + 2; j < limit; j++)
                {
                    if (line[j] == '\'') return j;
                }

                return -1;
            }

            return index + 1 < line.Length && line[index + 1] == '\'' ? index + 1 : -1;
        }

        #endregion

        #region Private-Classes

        private enum ScanModeEnum
        {
            Code,
            InterpolatedString,
            VerbatimInterpolatedString,
            RawString,
            TemplateLiteral,
            MultiLineString
        }

        private sealed class ScanFrame
        {
            public ScanFrame(ScanModeEnum mode)
            {
                Mode = mode;
            }

            public ScanModeEnum Mode { get; }

            public bool IsHole { get; set; } = false;

            public int Depth { get; set; } = 0;

            public int QuoteCount { get; set; } = 0;
        }

        /// <summary>
        /// Collects chunk ranges in file order, packing consecutive small pieces together.
        /// </summary>
        private sealed class ChunkBuilder
        {
            private int _PendingStart = -1;
            private int _PendingEnd = -1;

            public ChunkBuilder(string[] lines, int maxLines, int minStandaloneLines)
            {
                Lines = lines;
                MaxLines = maxLines;
                MinStandaloneLines = minStandaloneLines;
            }

            public string[] Lines { get; }

            public int MaxLines { get; }

            public int MinStandaloneLines { get; }

            public List<CodeChunkRange> Ranges { get; } = new List<CodeChunkRange>();

            /// <summary>Add a piece that fits the budget: alone when substantial, else packed with its small neighbours.</summary>
            public void AddPiece(int start, int endExclusive)
            {
                TrimBlankEdges(Lines, ref start, ref endExclusive);
                if (start >= endExclusive) return;

                if (CountNonBlank(start, endExclusive) >= MinStandaloneLines)
                {
                    Flush();
                    Ranges.Add(new CodeChunkRange(start, endExclusive));
                    return;
                }

                if (_PendingStart >= 0 && endExclusive - _PendingStart <= MaxLines)
                {
                    _PendingEnd = endExclusive;
                    return;
                }

                Flush();
                _PendingStart = start;
                _PendingEnd = endExclusive;
            }

            /// <summary>Add fixed windows over a span too large for one chunk.</summary>
            public void AddWindows(int start, int endExclusive)
            {
                Flush();
                AddLineWindows(Lines, start, endExclusive, MaxLines, Ranges, trimBlankEdges: true);
            }

            /// <summary>Emit the packed pending piece, if any.</summary>
            public void Flush()
            {
                if (_PendingStart < 0) return;
                Ranges.Add(new CodeChunkRange(_PendingStart, _PendingEnd));
                _PendingStart = -1;
                _PendingEnd = -1;
            }

            private int CountNonBlank(int start, int endExclusive)
            {
                int count = 0;
                for (int i = start; i < endExclusive; i++)
                {
                    if (Lines[i].Trim().Length > 0) count++;
                }

                return count;
            }
        }

        private sealed class BraceBlock
        {
            public BraceBlock(int openLine)
            {
                OpenLine = openLine;
            }

            public int OpenLine { get; }

            public int CloseLine { get; set; } = -1;

            public List<BraceBlock> Children { get; } = new List<BraceBlock>();
        }

        #endregion
    }
}
