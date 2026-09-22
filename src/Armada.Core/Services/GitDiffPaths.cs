namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using Armada.Core.Models;

    /// <summary>
    /// The single reader for file paths in Git output. Unified diffs are parsed per file so a
    /// deletion keeps its old path, a rename or copy keeps both paths, a binary entry keeps the
    /// paths from its header, and C-quoted names (<c>"r\303\251sum\303\251.md"</c>) are decoded to
    /// the real file name. NUL-separated output (<c>-z</c>) is split without any unquoting.
    /// Every caller that needs changed paths reads them here, so path rules see the same names.
    /// </summary>
    public static class GitDiffPaths
    {
        #region Public-Methods

        /// <summary>
        /// Parse every file entry of a unified diff. Accepts output with <c>diff --git</c> headers
        /// and plain unified diffs that only carry <c>---</c> / <c>+++</c> lines. Returns an empty
        /// list for null or empty input.
        /// </summary>
        /// <param name="unifiedDiff">Unified diff text.</param>
        /// <returns>File entries in the order they appear.</returns>
        public static IReadOnlyList<GitDiffFileChange> ParseFiles(string? unifiedDiff)
        {
            List<GitDiffFileChange> results = new List<GitDiffFileChange>();
            if (String.IsNullOrEmpty(unifiedDiff)) return results;

            FileBlock? current = null;
            int oldRemaining = 0;
            int newRemaining = 0;

            string[] lines = unifiedDiff.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.EndsWith("\r", StringComparison.Ordinal) ? rawLine.Substring(0, rawLine.Length - 1) : rawLine;

                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    Flush(current, results);
                    current = new FileBlock();
                    ParseGitHeader(line.Substring("diff --git ".Length), current);
                    oldRemaining = 0;
                    newRemaining = 0;
                    continue;
                }

                if (oldRemaining > 0 || newRemaining > 0)
                {
                    if (line.Length == 0 || line[0] == ' ')
                    {
                        oldRemaining--;
                        newRemaining--;
                        continue;
                    }

                    if (line[0] == '-')
                    {
                        oldRemaining--;
                        continue;
                    }

                    if (line[0] == '+')
                    {
                        newRemaining--;
                        continue;
                    }

                    if (line[0] == '\\') continue;

                    // A line that cannot belong to the hunk ends it; read it as header text.
                    oldRemaining = 0;
                    newRemaining = 0;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    ParseHunkCounts(line, out oldRemaining, out newRemaining);
                    if (current != null) current.SawHunk = true;
                    continue;
                }

                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    if (current == null || current.SawHunk || current.SawOldLine)
                    {
                        Flush(current, results);
                        current = new FileBlock();
                    }

                    current.SawOldLine = true;
                    string? oldPath = ParsePathField(line.Substring(4), "a/");
                    if (oldPath == null) current.Change.IsNew = true;
                    else current.Change.OldPath = oldPath;
                    continue;
                }

                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    if (current == null || current.SawHunk || current.SawNewLine)
                    {
                        Flush(current, results);
                        current = new FileBlock();
                    }

                    current.SawNewLine = true;
                    string? newPath = ParsePathField(line.Substring(4), "b/");
                    if (newPath == null) current.Change.IsDeleted = true;
                    else current.Change.NewPath = newPath;
                    continue;
                }

                if (current == null) continue;

                if (line.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    current.Change.IsRename = true;
                    current.Change.OldPath = DecodeQuotedPath(line.Substring("rename from ".Length));
                }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                {
                    current.Change.IsRename = true;
                    current.Change.NewPath = DecodeQuotedPath(line.Substring("rename to ".Length));
                }
                else if (line.StartsWith("copy from ", StringComparison.Ordinal))
                {
                    current.Change.IsCopy = true;
                    current.Change.OldPath = DecodeQuotedPath(line.Substring("copy from ".Length));
                }
                else if (line.StartsWith("copy to ", StringComparison.Ordinal))
                {
                    current.Change.IsCopy = true;
                    current.Change.NewPath = DecodeQuotedPath(line.Substring("copy to ".Length));
                }
                else if (line.StartsWith("new file mode", StringComparison.Ordinal))
                {
                    current.Change.IsNew = true;
                }
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
                {
                    current.Change.IsDeleted = true;
                }
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                         line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    current.Change.IsBinary = true;
                }
            }

            Flush(current, results);
            return results;
        }

        /// <summary>
        /// Every repository-relative path a unified diff touches: the old path of a deletion, the
        /// new path of an addition, both paths of a rename or copy. Distinct, in order of appearance.
        /// </summary>
        /// <param name="unifiedDiff">Unified diff text.</param>
        /// <returns>Touched paths.</returns>
        public static IReadOnlyList<string> ExtractPaths(string? unifiedDiff)
        {
            List<string> results = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (GitDiffFileChange change in ParseFiles(unifiedDiff))
            {
                AddPaths(change, results, seen);
            }

            return results;
        }

        /// <summary>
        /// Every path of one file entry: old path first, then the new path when it differs.
        /// </summary>
        /// <param name="change">Parsed file entry.</param>
        /// <returns>The entry's paths.</returns>
        public static IReadOnlyList<string> PathsOf(GitDiffFileChange change)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            List<string> results = new List<string>();
            AddPaths(change, results, new HashSet<string>(StringComparer.Ordinal));
            return results;
        }

        /// <summary>
        /// Split NUL-separated Git output (<c>--name-only -z</c>, <c>ls-files -z</c>). Names are
        /// raw, so no unquoting is applied; empty entries are dropped.
        /// </summary>
        /// <param name="output">Raw process output.</param>
        /// <returns>Paths in output order.</returns>
        public static IReadOnlyList<string> SplitNulSeparated(string? output)
        {
            List<string> results = new List<string>();
            if (String.IsNullOrEmpty(output)) return results;

            foreach (string entry in output.Split('\0'))
            {
                if (entry.Length == 0) continue;
                // A trailing newline can follow the last NUL when output passes through a shell.
                if (entry == "\n" || entry == "\r\n") continue;
                results.Add(entry);
            }

            return results;
        }

        /// <summary>
        /// Decode a Git C-quoted name (<c>"r\303\251sum\303\251.md"</c>, <c>"a\tb"</c>). Octal escapes
        /// are UTF-8 bytes. A value that is not wrapped in double quotes is returned unchanged.
        /// </summary>
        /// <param name="value">Name as Git printed it.</param>
        /// <returns>The real name.</returns>
        public static string DecodeQuotedPath(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') return value;
            int index = 0;
            return ReadQuoted(value, ref index);
        }

        #endregion

        #region Private-Methods

        private static void AddPaths(GitDiffFileChange change, List<string> results, HashSet<string> seen)
        {
            if (!String.IsNullOrEmpty(change.OldPath) && seen.Add(change.OldPath)) results.Add(change.OldPath);
            if (!String.IsNullOrEmpty(change.NewPath) && seen.Add(change.NewPath)) results.Add(change.NewPath);
        }

        private static void Flush(FileBlock? block, List<GitDiffFileChange> results)
        {
            if (block == null) return;
            GitDiffFileChange change = block.Change;
            if (change.IsNew) change.OldPath = null;
            if (change.IsDeleted) change.NewPath = null;
            if (String.IsNullOrEmpty(change.OldPath) && String.IsNullOrEmpty(change.NewPath)) return;
            results.Add(change);
        }

        private static void ParseHunkCounts(string line, out int oldCount, out int newCount)
        {
            // @@ -start[,count] +start[,count] @@
            oldCount = 0;
            newCount = 0;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (part.Length < 2) continue;
                if (part[0] == '-') oldCount = ParseRangeCount(part.Substring(1));
                else if (part[0] == '+') newCount = ParseRangeCount(part.Substring(1));
            }
        }

        private static int ParseRangeCount(string range)
        {
            int comma = range.IndexOf(',');
            if (comma < 0) return 1;
            return Int32.TryParse(range.Substring(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : 0;
        }

        private static string? ParsePathField(string value, string prefix)
        {
            string name;
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int index = 0;
                name = ReadQuoted(value, ref index);
            }
            else
            {
                // Git ends an unquoted name that contains a space with a tab; a real tab is always quoted.
                int tab = value.IndexOf('\t');
                name = tab >= 0 ? value.Substring(0, tab) : value;
            }

            if (name == "/dev/null") return null;
            if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name.Substring(prefix.Length);
            return name.Length == 0 ? null : name;
        }

        private static void ParseGitHeader(string remainder, FileBlock block)
        {
            string? first = null;
            string? second = null;

            if (remainder.StartsWith("\"", StringComparison.Ordinal))
            {
                int index = 0;
                first = ReadQuoted(remainder, ref index);
                string rest = index < remainder.Length ? remainder.Substring(index).TrimStart(' ') : String.Empty;
                second = rest.StartsWith("\"", StringComparison.Ordinal) ? DecodeQuotedPath(rest) : rest;
            }
            else
            {
                int quotedSecond = remainder.IndexOf(" \"", StringComparison.Ordinal);
                if (quotedSecond >= 0 && remainder.EndsWith("\"", StringComparison.Ordinal))
                {
                    first = remainder.Substring(0, quotedSecond);
                    second = DecodeQuotedPath(remainder.Substring(quotedSecond + 1));
                }
                else if (!SplitSameNameHeader(remainder, out first, out second))
                {
                    int split = remainder.IndexOf(" b/", StringComparison.Ordinal);
                    if (split >= 0)
                    {
                        first = remainder.Substring(0, split);
                        second = remainder.Substring(split + 1);
                    }
                    else
                    {
                        first = remainder;
                    }
                }
            }

            block.Change.OldPath = StripPrefix(first, "a/");
            block.Change.NewPath = StripPrefix(second, "b/");
        }

        private static bool SplitSameNameHeader(string remainder, out string? first, out string? second)
        {
            // "a/<name> b/<name>" with an unquoted name that may contain spaces: when both sides
            // name the same file the split point is the exact middle.
            first = null;
            second = null;
            if (remainder.Length < 5 || remainder.Length % 2 == 0) return false;
            int middle = remainder.Length / 2;
            if (remainder[middle] != ' ') return false;
            string left = remainder.Substring(0, middle);
            string right = remainder.Substring(middle + 1);
            if (!left.StartsWith("a/", StringComparison.Ordinal) || !right.StartsWith("b/", StringComparison.Ordinal)) return false;
            if (!String.Equals(left.Substring(2), right.Substring(2), StringComparison.Ordinal)) return false;
            first = left;
            second = right;
            return true;
        }

        private static string? StripPrefix(string? token, string prefix)
        {
            if (String.IsNullOrEmpty(token)) return null;
            if (token == "/dev/null") return null;
            return token.StartsWith(prefix, StringComparison.Ordinal) ? token.Substring(prefix.Length) : token;
        }

        private static string ReadQuoted(string value, ref int index)
        {
            // value[index] is the opening quote. Reads through the closing quote and leaves index after it.
            List<byte> bytes = new List<byte>();
            index++;
            while (index < value.Length)
            {
                char c = value[index];
                if (c == '"')
                {
                    index++;
                    break;
                }

                if (c != '\\' || index + 1 >= value.Length)
                {
                    AppendUtf8(bytes, value, ref index);
                    continue;
                }

                char escape = value[index + 1];
                if (escape >= '0' && escape <= '3' && index + 3 < value.Length &&
                    IsOctal(value[index + 2]) && IsOctal(value[index + 3]))
                {
                    int octal = ((escape - '0') << 6) | ((value[index + 2] - '0') << 3) | (value[index + 3] - '0');
                    bytes.Add((byte)octal);
                    index += 4;
                    continue;
                }

                switch (escape)
                {
                    case 'a': bytes.Add(7); break;
                    case 'b': bytes.Add(8); break;
                    case 't': bytes.Add(9); break;
                    case 'n': bytes.Add(10); break;
                    case 'v': bytes.Add(11); break;
                    case 'f': bytes.Add(12); break;
                    case 'r': bytes.Add(13); break;
                    case '"': bytes.Add((byte)'"'); break;
                    case '\\': bytes.Add((byte)'\\'); break;
                    default:
                        bytes.Add((byte)'\\');
                        index++;
                        continue;
                }

                index += 2;
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static void AppendUtf8(List<byte> bytes, string value, ref int index)
        {
            int length = Char.IsHighSurrogate(value[index]) && index + 1 < value.Length ? 2 : 1;
            bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(index, length)));
            index += length;
        }

        private static bool IsOctal(char c)
        {
            return c >= '0' && c <= '7';
        }

        #endregion

        #region Private-Classes

        private sealed class FileBlock
        {
            public GitDiffFileChange Change { get; } = new GitDiffFileChange();

            public bool SawHunk { get; set; }

            public bool SawOldLine { get; set; }

            public bool SawNewLine { get; set; }
        }

        #endregion
    }
}
