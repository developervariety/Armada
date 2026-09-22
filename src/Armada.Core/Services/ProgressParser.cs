namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;

    /// <summary>
    /// Parses agent output for progress signals.
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
