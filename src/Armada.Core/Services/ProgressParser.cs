namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;

    /// <summary>
    /// Parses agent output for progress signals, and is the one definition of where a protocol marker counts:
    /// only at the start of a physical line (leading whitespace allowed). A marker in the middle of a prose line,
    /// or glued to the end of other text ("Done.[ARMADA:RESULT] COMPLETE"), is not a marker for any reader. The
    /// runtimes deliver captain text so that a real marker starts its own line: they write each text block as its
    /// own record and join streamed text pieces into whole lines before a record is written.
    /// Agents can emit lines like:
    ///   [ARMADA:PROGRESS] 75
    ///   [ARMADA:STATUS] Testing
    ///   [ARMADA:RESULT] COMPLETE
    ///   [ARMADA:VERDICT] PASS
    ///   [ARMADA:MESSAGE] Running unit tests now
    ///   [ARMADA:PAPERCUT] {"category":"MissingDoc","severity":"Low","title":"..."}
    /// </summary>
    public static class ProgressParser
    {
        #region Public-Members

        /// <summary>
        /// Parsed progress signal from agent output.
        /// </summary>
        public class ProgressSignal
        {
            /// <summary>
            /// Signal type: "progress", "status", "message", "result", or "verdict".
            /// </summary>
            public string Type { get; set; } = "";

            /// <summary>
            /// Signal value.
            /// </summary>
            public string Value { get; set; } = "";

            /// <summary>
            /// Parsed percentage (0-100) if type is "progress".
            /// </summary>
            public int? Percentage { get; set; } = null;

            /// <summary>
            /// Parsed mission status if type is "status".
            /// </summary>
            public MissionStatusEnum? MissionStatus { get; set; } = null;
        }

        #endregion

        #region Private-Members

        private static readonly Regex _SignalPattern = new Regex(
            @"^\s*\[ARMADA:(\w+)\]\s+(.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Parse every progress signal in an agent output record, in the order they appear.
        /// Returns an empty list if the record contains no signal.
        /// </summary>
        /// <remarks>
        /// A runtime record is not always a single line: a Codex <c>agent_message</c> or a Claude
        /// assistant text block can carry several physical lines in one record, and a captain's
        /// final answer commonly holds several markers (a message or papercut followed by
        /// <c>[ARMADA:RESULT] COMPLETE</c>). Each marker is returned so a caller can route every one;
        /// none hides another. A marker only counts when it STARTS a physical line -- a signal
        /// embedded in the middle of a prose line is not a signal (for example an instruction file
        /// that documents the format).
        /// </remarks>
        /// <param name="record">Agent output record.</param>
        /// <returns>Parsed signals in record order; empty when there are none.</returns>
        public static List<ProgressSignal> ParseAll(string? record)
        {
            List<ProgressSignal> signals = new List<ProgressSignal>();
            if (String.IsNullOrEmpty(record)) return signals;

            foreach (string physicalLine in record.Split('\n'))
            {
                ProgressSignal? parsed = TryParsePhysicalLine(physicalLine);
                if (parsed != null) signals.Add(parsed);
            }

            return signals;
        }

        /// <summary>
        /// Parse the first progress signal in an agent output record.
        /// Returns null if the record does not contain a signal.
        /// </summary>
        /// <remarks>
        /// A record can hold several markers; this returns only the first. A caller that acts on
        /// markers of more than one type uses <see cref="ParseAll"/> so no marker is lost.
        /// </remarks>
        /// <param name="line">Agent output record.</param>
        /// <returns>First parsed signal or null.</returns>
        public static ProgressSignal? TryParse(string line)
        {
            List<ProgressSignal> signals = ParseAll(line);
            return signals.Count > 0 ? signals[0] : null;
        }

        /// <summary>
        /// True when the output holds a terminal marker (<see cref="TerminalMarkerTracker.IsTerminalMarker"/>) at the
        /// start of a line: the captain's claim that its stage finished.
        /// </summary>
        /// <param name="output">Agent output.</param>
        /// <returns>True when a terminal marker starts a line.</returns>
        public static bool HasTerminalMarker(string? output)
        {
            foreach (ProgressSignal signal in ParseAll(output))
            {
                if (TerminalMarkerTracker.IsTerminalMarker(signal)) return true;
            }

            return false;
        }

        /// <summary>
        /// True when the output holds a marker of one of the given types (for example "result" or "verdict") at the
        /// start of a line, whatever its value.
        /// </summary>
        /// <param name="output">Agent output.</param>
        /// <param name="types">Lower-case signal types.</param>
        /// <returns>True when such a marker starts a line.</returns>
        public static bool HasSignal(string? output, params string[] types)
        {
            foreach (ProgressSignal signal in ParseAll(output))
            {
                foreach (string type in types)
                {
                    if (String.Equals(signal.Type, type, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Find a marker of the given type whose value begins with the given word (for example a
        /// <c>[ARMADA:RESULT] REFUSED &lt;reason&gt;</c> line), searching forward or from the end.
        /// </summary>
        /// <param name="output">Agent output.</param>
        /// <param name="type">Lower-case signal type.</param>
        /// <param name="leadingWord">Word the marker value must begin with, compared without case.</param>
        /// <param name="fromEnd">True to return the last such marker, false for the first.</param>
        /// <param name="match">The marker found.</param>
        /// <returns>True when such a marker starts a line.</returns>
        public static bool TryFindSignal(string? output, string type, string leadingWord, bool fromEnd, out MarkerLine match)
        {
            match = new MarkerLine();
            if (String.IsNullOrEmpty(output)) return false;

            List<MarkerLine> found = new List<MarkerLine>();
            int offset = 0;
            foreach (string physicalLine in output.Split('\n'))
            {
                ProgressSignal? signal = TryParsePhysicalLine(physicalLine);
                if (signal != null
                    && String.Equals(signal.Type, type, StringComparison.OrdinalIgnoreCase)
                    && StartsWithWord(signal.Value, leadingWord))
                {
                    found.Add(new MarkerLine
                    {
                        Line = physicalLine.Trim(),
                        LineStart = offset,
                        Remainder = signal.Value.Substring(leadingWord.Length).Trim()
                    });
                    if (!fromEnd) break;
                }

                offset += physicalLine.Length + 1;
            }

            if (found.Count == 0) return false;
            match = fromEnd ? found[found.Count - 1] : found[0];
            return true;
        }

        /// <summary>
        /// A marker line found by <see cref="TryFindSignal"/>.
        /// </summary>
        public sealed class MarkerLine
        {
            /// <summary>The physical line, trimmed.</summary>
            public string Line { get; set; } = "";

            /// <summary>Character index in the output where the physical line starts.</summary>
            public int LineStart { get; set; }

            /// <summary>The marker value after its leading word, trimmed.</summary>
            public string Remainder { get; set; } = "";
        }

        private static bool StartsWithWord(string value, string word)
        {
            if (String.IsNullOrEmpty(value) || !value.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return false;
            if (value.Length == word.Length) return true;
            char next = value[word.Length];
            return !Char.IsLetterOrDigit(next) && next != '_';
        }

        private static ProgressSignal? TryParsePhysicalLine(string line)
        {
            if (String.IsNullOrEmpty(line)) return null;

            Match match = _SignalPattern.Match(line);
            if (!match.Success) return null;

            string type = match.Groups[1].Value.ToLowerInvariant();
            string value = match.Groups[2].Value.Trim();

            ProgressSignal signal = new ProgressSignal
            {
                Type = type,
                Value = value
            };

            switch (type)
            {
                case "progress":
                    if (int.TryParse(value.TrimEnd('%'), out int pct))
                    {
                        signal.Percentage = Math.Clamp(pct, 0, 100);
                    }
                    break;

                case "status":
                    if (Enum.TryParse<MissionStatusEnum>(value, true, out MissionStatusEnum status))
                    {
                        signal.MissionStatus = status;
                    }
                    break;
            }

            return signal;
        }

        #endregion
    }
}
