namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Deterministic keep-and-drop rules for large command output. Diagnostic lines, test totals,
    /// structured documents and binary blobs stay; repetitive progress may drop. These are shape
    /// tests, not meaning judgments: a regex scored 100% on this class of line where a classifier
    /// scored 25-75%, so they belong here and not in a typed decision.
    /// </summary>
    public static class ToolOutputRetention
    {
        #region Public-Members

        /// <summary>Estimated-token floor below which output is left whole. Matches the inbound prune
        /// that scored this class of log: a 10,000-token cut, not a byte cap.</summary>
        public const int MinimumOutputTokens = 10_000;

        /// <summary>Characters treated as one estimated token, matching the inbound prune's estimator.</summary>
        public const int CharsPerToken = 4;

        /// <summary>Lines grouped into one prune chunk. A single needed line protects its whole chunk.</summary>
        public const int ChunkLines = 20;

        #endregion

        #region Private-Members

        private const int _MaxChunks = 200;
        private const int _MaxLineChars = 2_000;
        private const int _DistinctiveLineChars = 240;

        private static readonly TimeSpan _MatchTimeout = TimeSpan.FromMilliseconds(50);

        private static readonly Regex _Diagnostic = new Regex(
            @"\b(ERROR|FATAL|FAILED|FAILURE|PANIC|WARN|WARNING)\b"
            + @"|\b(error|warning|failure|exception|panic|traceback|assertion)s?\s*:"
            + @"|\berror TS\d+:|^E\s+\S"
            + @"|\b(failed|failing|cannot|could not|unable to|denied|refused|timed out)\s+\w"
            + @"|\b\w*(Error|Exception)\b\s*[:(]"
            + @"|\bTraceback \(most recent call last\)"
            + @"|^\s*at\s+\S+\(.*:\d+"
            + @"|\b(severity )?vulnerabilit(y|ies)\b"
            + @"|\bCrashLoopBackOff\b|\bOOMKilled\b"
            + @"|\bHTTP/[0-9.]+ [45]\d\d\b|\bstatus[=: ]\s*[45]\d\d\b"
            + @"|\bExpected:\s|\bActual:\s|\bAssert\.\w+",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant,
            _MatchTimeout);

        private static readonly Regex _Result = new Regex(
            @"^\s*(?:(?:Test Suites|Tests|Snapshots|Coverage|Results?|Summary|Exit code|Exit status|Total tests)\s*:|(?:Build|Compilation|Tests?)\s+(?:succeeded|completed|finished|passed|failed)\b|(?:Artifact|Output file|Report|Coverage report)(?: path)?\s*[:=]\s*\S|Passed!\s*-|Failed:\s*\d+)",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            _MatchTimeout);

        private static readonly Regex _PytestResult = new Regex(
            @"^=+ .*\b\d+ (?:passed|failed|skipped|deselected|xfailed|xpassed|errors?|warnings?)\b.*=+\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            _MatchTimeout);

        private static readonly Regex _TestProgress = new Regex(
            @"^\S+::\S+\s+PASSED(?:\s+\[\s*\d+%\])?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            _MatchTimeout);

        private static readonly Regex _Progress = new Regex(
            @"^\s*(?:\[[^\]\n]+\]\s*)?(?:INFO\s+)?(?:progress\b|cache(?:d)?\b|download(?:ing)?\b|compil(?:ing|ed)\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            _MatchTimeout);

        private static readonly Regex _Reference = new Regex(
            @"^#{1,6} +\S|^```|^~~~"
            + @"|^\s*(?:export\s+)?(?:async\s+)?(?:function|class|def)\s+\w"
            + @"|^\s*(?:from\s+[\w.]+\s+import|import\s+.+)"
            + @"|^\s*#include\s*[<""]",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant,
            _MatchTimeout);

        private static readonly Regex _DiffCue = new Regex(
            @"^diff --git |^--- |^@@ ",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant,
            _MatchTimeout);

        private static readonly Regex _DocumentCommand = new Regex(
            @"^(cat|bat|jq|yq|diff|git\s+(diff|show)|base64|openssl)(?:\s|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            _MatchTimeout);

        private static readonly Regex _DocumentPipeline = new Regex(
            @"(^|[|;&]\s*)(cat|bat|jq|yq|git\s+(diff|show)|base64|openssl)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            _MatchTimeout);

        #endregion

        #region Public-Methods

        /// <summary>Estimated token count for a string, at <see cref="CharsPerToken"/> characters each.</summary>
        /// <param name="text">Text to measure.</param>
        /// <returns>The estimated token count, at least zero.</returns>
        public static int EstimateTokens(string? text)
        {
            if (String.IsNullOrEmpty(text)) return 0;
            return (text.Length + CharsPerToken - 1) / CharsPerToken;
        }

        /// <summary>
        /// Whether the text carries a diagnostic or a result total. Compaction and prune both ask
        /// this one predicate, so a test-failure line cannot be kept by one and dropped by the other.
        /// </summary>
        /// <param name="text">Line or block to classify.</param>
        /// <returns>True when a diagnostic or result pattern matches.</returns>
        public static bool IsProtected(string? text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            try
            {
                return _Diagnostic.IsMatch(text) || _Result.IsMatch(text) || _PytestResult.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                // A timeout is treated as protected: losing a diagnostic is worse than keeping noise.
                return true;
            }
        }

        /// <summary>The first protected line in a block, bounded, or null when none matches.</summary>
        /// <param name="text">Block to scan.</param>
        /// <returns>The first matching line, or null.</returns>
        public static string? FirstProtectedLine(string? text)
        {
            return ScanProtectedLine(text, fromEnd: false);
        }

        /// <summary>The last protected line in a block, bounded, or null when none matches.</summary>
        /// <param name="text">Block to scan.</param>
        /// <returns>The last matching line, or null.</returns>
        public static string? LastProtectedLine(string? text)
        {
            return ScanProtectedLine(text, fromEnd: true);
        }

        /// <summary>
        /// Distinctive lines a compaction candidate should show the model: the first and last
        /// protected lines, which is where assertion text and test totals live. Empty when the
        /// block has none, so a settled listing stays a head-only candidate.
        /// </summary>
        /// <param name="text">Full tool output.</param>
        /// <returns>One or two bounded lines, or an empty string.</returns>
        public static string DistinctiveExcerpt(string? text)
        {
            string? first = FirstProtectedLine(text);
            string? last = LastProtectedLine(text);
            if (first == null && last == null) return String.Empty;
            if (last == null || String.Equals(first, last, StringComparison.Ordinal)) return first ?? String.Empty;
            if (first == null) return last;
            return first + "\n" + last;
        }

        /// <summary>
        /// Output with NULs or a lot of control bytes is not text worth chunking, so prune leaves it.
        /// </summary>
        /// <param name="output">Command output.</param>
        /// <returns>True when the sample looks binary.</returns>
        public static bool LooksBinary(string? output)
        {
            if (String.IsNullOrEmpty(output)) return false;
            if (output.IndexOf('\0') >= 0) return true;
            int sampleLength = Math.Min(output.Length, 4000);
            int control = 0;
            for (int index = 0; index < sampleLength; index++)
            {
                char ch = output[index];
                if (ch < 9 || (ch > 13 && ch < 32) || ch == 127) control++;
            }
            return control > sampleLength * 0.05;
        }

        /// <summary>
        /// Output the captain is likely to parse as one document (a file dump, a diff, a JSON blob).
        /// Cutting a hole in it leaves something that still looks complete but is not.
        /// </summary>
        /// <param name="command">The shell command that produced the output.</param>
        /// <param name="output">The output.</param>
        /// <returns>True when the output must stay whole.</returns>
        public static bool LooksStructured(string? command, string? output)
        {
            string text = output ?? String.Empty;
            string head = text.TrimStart();
            if (head.StartsWith('{') || head.StartsWith('['))
            {
                try
                {
                    System.Text.Json.JsonDocument.Parse(text);
                    return true;
                }
                catch (System.Text.Json.JsonException)
                {
                    // Not JSON after all.
                }
            }
            if (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("---\n", StringComparison.Ordinal))
                return true;
            if (head.Length > 1 && head[0] == '<' && Char.IsLetter(head[1])) return true;
            try
            {
                if (_DiffCue.IsMatch(text)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }

            string simple = SimpleCommand(command ?? String.Empty);
            try
            {
                if (!String.IsNullOrEmpty(simple) && _DocumentCommand.IsMatch(simple)) return true;
                if (_DocumentPipeline.IsMatch(command ?? String.Empty)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }

            try
            {
                if (_Reference.IsMatch(text)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Drop progress-only chunks from a large command log. Diagnostics, results, unknown text,
        /// the first chunk and the last chunk stay. Below the token floor, a binary blob, a structured
        /// document, or a prune that would drop nothing, the original output is returned unchanged.
        /// </summary>
        /// <param name="command">The shell command that produced the output.</param>
        /// <param name="output">The output to consider.</param>
        /// <returns>The kept output and whether anything was dropped.</returns>
        public static ToolOutputPruneResult Prune(string? command, string? output)
        {
            string text = output ?? String.Empty;
            if (EstimateTokens(text) <= MinimumOutputTokens)
                return ToolOutputPruneResult.Unchanged(text, "below_threshold");
            if (LooksBinary(text))
                return ToolOutputPruneResult.Unchanged(text, "binary");
            if (LooksStructured(command, text))
                return ToolOutputPruneResult.Unchanged(text, "document");

            List<string> chunks = SplitChunks(text);
            if (chunks.Count <= 2)
                return ToolOutputPruneResult.Unchanged(text, "few_chunks");

            List<string> kept = new List<string>();
            int dropped = 0;
            for (int index = 0; index < chunks.Count; index++)
            {
                string chunk = chunks[index];
                bool pin = index == 0 || index == chunks.Count - 1;
                if (pin || !IsProgressOnly(chunk) || IsProtected(chunk))
                {
                    kept.Add(chunk);
                    continue;
                }

                dropped++;
                if (kept.Count == 0 || !kept[kept.Count - 1].StartsWith("[... ", StringComparison.Ordinal))
                    kept.Add("[... progress omitted ...]\n");
            }

            if (dropped == 0)
                return ToolOutputPruneResult.Unchanged(text, "kept_all");

            StringBuilder rendered = new StringBuilder();
            rendered.Append("[pruned; retained lines verbatim; omissions marked]\n");
            foreach (string chunk in kept) rendered.Append(chunk);
            string pruned = rendered.ToString();
            if (pruned.Length >= text.Length)
                return ToolOutputPruneResult.Unchanged(text, "kept_all");

            return new ToolOutputPruneResult(pruned, true, chunks.Count, dropped, "pruned");
        }

        #endregion

        #region Private-Methods

        private static string? ScanProtectedLine(string? text, bool fromEnd)
        {
            if (String.IsNullOrEmpty(text)) return null;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (fromEnd)
            {
                for (int index = lines.Length - 1; index >= 0; index--)
                    if (IsProtected(lines[index])) return Truncate(lines[index], _DistinctiveLineChars);
            }
            else
            {
                for (int index = 0; index < lines.Length; index++)
                    if (IsProtected(lines[index])) return Truncate(lines[index], _DistinctiveLineChars);
            }
            return null;
        }

        private static bool IsProgressOnly(string chunk)
        {
            bool any = false;
            foreach (string line in chunk.Replace("\r\n", "\n").Split('\n'))
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                any = true;
                try
                {
                    if (_Progress.IsMatch(line) || _TestProgress.IsMatch(line)) continue;
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
                return false;
            }
            return any;
        }

        private static List<string> SplitChunks(string output)
        {
            List<string> lines = new List<string>();
            foreach (string line in output.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length <= _MaxLineChars)
                {
                    lines.Add(line);
                    continue;
                }
                for (int at = 0; at < line.Length; at += _MaxLineChars)
                    lines.Add(line.Substring(at, Math.Min(_MaxLineChars, line.Length - at)));
            }

            List<string> chunks = new List<string>();
            StringBuilder current = new StringBuilder();
            int currentLines = 0;
            foreach (string line in lines)
            {
                if (chunks.Count >= _MaxChunks)
                {
                    // Remainder joins the last chunk so a trailing diagnostic is not dropped by the cap.
                    chunks[chunks.Count - 1] = chunks[chunks.Count - 1] + line + "\n";
                    continue;
                }

                current.Append(line).Append('\n');
                currentLines++;
                if (currentLines >= ChunkLines)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                    currentLines = 0;
                }
            }
            if (currentLines > 0)
            {
                if (chunks.Count >= _MaxChunks)
                    chunks[chunks.Count - 1] = chunks[chunks.Count - 1] + current.ToString();
                else
                    chunks.Add(current.ToString());
            }
            return chunks;
        }

        private static string SimpleCommand(string command)
        {
            if (Regex.IsMatch(command, @"[\r\n|;&<>`$\\]")) return String.Empty;
            string trimmed = command.Trim();
            trimmed = Regex.Replace(trimmed, @"^(?:[A-Za-z_]\w*=(?:[^\s'""]+|'[^']*'|""[^""]*"")\s+)*", String.Empty);
            trimmed = Regex.Replace(trimmed, @"^(?:/?[\w.-]+/)+", String.Empty);
            return trimmed;
        }

        private static string Truncate(string text, int maximumChars)
        {
            if (text.Length <= maximumChars) return text;
            return text.Substring(0, maximumChars);
        }

        #endregion
    }

    /// <summary>The outcome of a deterministic prune pass.</summary>
    public sealed class ToolOutputPruneResult
    {
        /// <summary>Create a prune result.</summary>
        /// <param name="output">Output the caller should keep.</param>
        /// <param name="pruned">Whether any chunk was dropped.</param>
        /// <param name="chunks">Chunk count considered.</param>
        /// <param name="dropped">Chunks dropped.</param>
        /// <param name="reason">Short reason label.</param>
        public ToolOutputPruneResult(string output, bool pruned, int chunks, int dropped, string reason)
        {
            Output = output ?? String.Empty;
            Pruned = pruned;
            Chunks = chunks;
            Dropped = dropped;
            Reason = reason ?? String.Empty;
        }

        /// <summary>Output the caller should keep.</summary>
        public string Output { get; }

        /// <summary>Whether any chunk was dropped.</summary>
        public bool Pruned { get; }

        /// <summary>Chunk count considered.</summary>
        public int Chunks { get; }

        /// <summary>Chunks dropped.</summary>
        public int Dropped { get; }

        /// <summary>Short reason label.</summary>
        public string Reason { get; }

        /// <summary>A result that leaves the original output in place.</summary>
        /// <param name="output">Original output.</param>
        /// <param name="reason">Why nothing was dropped.</param>
        /// <returns>An unchanged result.</returns>
        public static ToolOutputPruneResult Unchanged(string output, string reason)
        {
            return new ToolOutputPruneResult(output, false, 0, 0, reason);
        }
    }
}
