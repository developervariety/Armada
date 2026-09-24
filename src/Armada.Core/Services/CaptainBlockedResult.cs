namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The one rule for a captain that ends its stage blocked. A stage is blocked when its final outcome
    /// marker (<see cref="ProgressParser.TryFindFinalOutcome"/>: the last <c>[ARMADA:RESULT]</c> or
    /// <c>[ARMADA:VERDICT]</c> that starts a line) is <c>[ARMADA:RESULT] BLOCKED</c>. Prose that mentions
    /// BLOCKED, a marker in the middle of a line, and a BLOCKED marker followed by a later result or verdict
    /// are not blocked.
    /// </summary>
    /// <remarks>
    /// A blocked stage asks a question only the owner can answer. It did not finish, so it does not
    /// complete, does not hand off, and does not land, and a rescue that re-runs the same stage without
    /// the answer would ask the same question again. The completion path fails the mission with a
    /// <see cref="FailureReasonPrefix"/> reason that carries the question, and recovery reads that prefix
    /// to hold the rescue.
    /// </remarks>
    public static class CaptainBlockedResult
    {
        #region Public-Members

        /// <summary>The failure reason prefix of a mission whose captain ended its stage blocked.</summary>
        public const string FailureReasonPrefix = "captain_blocked:";

        /// <summary>The line that introduces the captain's question inside the failure reason.</summary>
        public const string QuestionHeading = "Question:";

        /// <summary>The largest question text kept, in characters.</summary>
        public const int MaxQuestionLength = 4000;

        /// <summary>The leading word of the blocked result marker.</summary>
        public const string BlockedWord = "BLOCKED";

        #endregion

        #region Private-Members

        // When the captain writes its question above the marker, this many lines before it are the question.
        private const int _PrecedingQuestionLines = 20;

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when the output's final outcome marker is <c>[ARMADA:RESULT] BLOCKED</c> at the start of a line.
        /// </summary>
        /// <param name="agentOutput">Captain output.</param>
        /// <returns>True when the stage ended blocked.</returns>
        public static bool IsBlocked(string? agentOutput)
        {
            return TryFindMarker(agentOutput, out ProgressParser.MarkerLine _);
        }

        /// <summary>
        /// Find the final <c>[ARMADA:RESULT] BLOCKED</c> marker, when it is the output's final outcome.
        /// </summary>
        /// <param name="agentOutput">Captain output.</param>
        /// <param name="marker">The blocked marker line.</param>
        /// <returns>True when the stage ended blocked.</returns>
        public static bool TryFindMarker(string? agentOutput, out ProgressParser.MarkerLine marker)
        {
            marker = new ProgressParser.MarkerLine();
            if (!ProgressParser.TryFindFinalOutcome(agentOutput, out ProgressParser.ProgressSignal signal, out ProgressParser.MarkerLine final))
                return false;
            if (!String.Equals(signal.Type, "result", StringComparison.Ordinal)) return false;
            if (!ProgressParser.ValueStartsWithWord(signal.Value, BlockedWord)) return false;

            final.Remainder = signal.Value.Substring(BlockedWord.Length).Trim().TrimStart(':', '-').Trim();
            marker = final;
            return true;
        }

        /// <summary>
        /// Read the captain's question when the stage ended blocked. The question is the text on the marker line
        /// and after it; when the captain wrote nothing there, it is the lines just above the marker, where the
        /// captain explained the blocker. Bounded to <see cref="MaxQuestionLength"/> characters.
        /// </summary>
        /// <param name="agentOutput">Captain output.</param>
        /// <param name="question">The question text, never empty when the stage is blocked.</param>
        /// <returns>True when the stage ended blocked.</returns>
        public static bool TryRead(string? agentOutput, out string question)
        {
            question = String.Empty;
            if (!TryFindMarker(agentOutput, out ProgressParser.MarkerLine marker)) return false;

            string normalizedLine = marker.Line;
            int lineEnd = agentOutput!.IndexOf('\n', marker.LineStart);
            string after = lineEnd < 0 ? String.Empty : agentOutput.Substring(lineEnd + 1).Replace("\r\n", "\n").Trim();

            List<string> parts = new List<string>();
            if (!String.IsNullOrWhiteSpace(marker.Remainder)) parts.Add(marker.Remainder);
            if (!String.IsNullOrWhiteSpace(after)) parts.Add(after);

            if (parts.Count > 0)
            {
                question = Truncate(String.Join("\n", parts), keepEnd: false);
            }
            else
            {
                string before = agentOutput.Substring(0, marker.LineStart).Replace("\r\n", "\n").TrimEnd();
                string[] lines = before.Split('\n');
                int first = Math.Max(0, lines.Length - _PrecedingQuestionLines);
                string tail = String.Join("\n", lines, first, lines.Length - first).Trim();
                question = String.IsNullOrWhiteSpace(tail)
                    ? "The captain ended with " + normalizedLine + " and stated no question."
                    : Truncate(tail, keepEnd: true);
            }

            return true;
        }

        /// <summary>
        /// Build the failure reason for a blocked stage. It names the class, the persona, and carries the question.
        /// </summary>
        /// <param name="persona">Stage persona.</param>
        /// <param name="question">The captain's question.</param>
        /// <returns>The failure reason.</returns>
        public static string BuildFailureReason(string? persona, string question)
        {
            string stage = String.IsNullOrWhiteSpace(persona) ? "Worker" : persona!.Trim();
            return FailureReasonPrefix + " the " + stage + " stage ended with [ARMADA:RESULT] BLOCKED on a question only the owner can answer."
                + " The stage did not complete and was not handed off or retried; answer the question, then re-dispatch.\n"
                + QuestionHeading + "\n" + (question ?? String.Empty);
        }

        /// <summary>
        /// True when a mission failure reason records a blocked stage.
        /// </summary>
        /// <param name="failureReason">Mission failure reason.</param>
        /// <returns>True for a blocked stage.</returns>
        public static bool IsBlockedFailure(string? failureReason)
        {
            return !String.IsNullOrEmpty(failureReason)
                && failureReason!.StartsWith(FailureReasonPrefix, StringComparison.Ordinal);
        }

        #endregion

        #region Private-Methods

        private static string Truncate(string text, bool keepEnd)
        {
            if (text.Length <= MaxQuestionLength) return text;
            return keepEnd
                ? "..." + text.Substring(text.Length - MaxQuestionLength)
                : text.Substring(0, MaxQuestionLength) + "...";
        }

        #endregion
    }
}
