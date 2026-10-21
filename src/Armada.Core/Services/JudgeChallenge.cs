namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>
    /// The deterministic half of the challenge round: which Judge failures may be challenged, how the
    /// blocking findings are numbered, what the Challenger is told, and how its rulings are settled.
    /// </summary>
    /// <remarks>
    /// The Challenger only narrows what a rescue is asked to fix. It never passes work: a block whose
    /// every finding is refuted waits for an operator, and the Judge mission stays failed. A finding the
    /// Challenger does not rule on, rules on without evidence, or refutes below the confidence floor
    /// stands. Output that cannot be settled makes the whole challenge unavailable, and the rescue then
    /// carries every finding, as it does with the round switched off.
    /// </remarks>
    public static class JudgeChallenge
    {
        #region Public-Members

        /// <summary>
        /// Marker type of the Challenger's ruling line, lower-cased as <see cref="ProgressParser"/> reports it.
        /// </summary>
        public const string SignalType = "challenge";

        /// <summary>
        /// Failure reason prefix of a Judge NEEDS_REVISION, the only verdict the round challenges.
        /// </summary>
        public const string NeedsRevisionReason = "Judge verdict: NEEDS_REVISION";

        /// <summary>
        /// Bound on the extracted findings text. A review whose blocking findings do not fit is not challenged.
        /// </summary>
        public const int MaxFindingsChars = 12000;

        /// <summary>
        /// Bound on the Judge output embedded in the Challenger brief.
        /// </summary>
        public const int MaxJudgeOutputChars = 30000;

        /// <summary>
        /// Bound on the reviewed mission description embedded in the Challenger brief.
        /// </summary>
        public const int MaxReviewedBriefChars = 20000;

        /// <summary>Event recorded when a Challenger is dispatched.</summary>
        public const string DispatchedEvent = "judge_challenge.dispatched";

        /// <summary>Event recorded when the Challenger's rulings are settled.</summary>
        public const string RuledEvent = "judge_challenge.ruled";

        /// <summary>Event recorded when the challenge cannot be used and the rescue carries every finding.</summary>
        public const string UnavailableEvent = "judge_challenge.unavailable";

        /// <summary>Event recorded when an enabled round declines to challenge a Judge failure.</summary>
        public const string SkippedEvent = "judge_challenge.skipped";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a failed mission is a Judge NEEDS_REVISION the round may challenge. A FAIL verdict, a
        /// PASS rejected by a red Check, a criterion conflict and a comment-only revision are never challenged.
        /// </summary>
        /// <param name="mission">Failed mission.</param>
        /// <returns>True when the failure is a challengeable Judge NEEDS_REVISION.</returns>
        public static bool IsChallengeableFailure(Mission? mission)
        {
            if (mission == null) return false;
            if (!PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge)) return false;
            string reason = mission.FailureReason ?? String.Empty;
            if (!reason.StartsWith(NeedsRevisionReason, StringComparison.Ordinal)) return false;
            if (reason.Contains(JudgeCriterionConflicts.RescueBlockMarker, StringComparison.Ordinal)) return false;
            if (reason.Contains(MissionService.RevisionCommentOnlyRescueBlockMarker, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// Number the blocking findings of a review, using the same extraction the rescue brief uses.
        /// </summary>
        /// <param name="review">Complete Judge output.</param>
        /// <param name="complete">False when the extraction could not hold every finding.</param>
        /// <returns>Findings in review order; finding N is element N-1.</returns>
        public static List<string> ExtractFindings(string? review, out bool complete)
        {
            complete = true;
            List<string> findings = new List<string>();
            string text = StageReportEssentials.BlockingFindings(review, MaxFindingsChars);
            if (text.Contains(StageReportEssentials.IncompleteFindingsMarker, StringComparison.Ordinal))
                complete = false;

            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Contains(StageReportEssentials.IncompleteFindingsMarker, StringComparison.Ordinal)) continue;
                if (line.StartsWith("- ", StringComparison.Ordinal)) line = line.Substring(2).Trim();
                if (line.Length > 0) findings.Add(line);
            }

            return findings;
        }

        /// <summary>
        /// Whether a finding may never be refuted: it contains a configured guardrail marker, or it names
        /// one of the vessel's protected paths.
        /// </summary>
        /// <param name="finding">Finding text.</param>
        /// <param name="markers">Configured unchallengeable markers.</param>
        /// <param name="protectedPaths">Vessel protected path patterns.</param>
        /// <returns>True when the finding always stands.</returns>
        public static bool IsUnchallengeable(string finding, IEnumerable<string>? markers, IEnumerable<string>? protectedPaths)
        {
            if (String.IsNullOrEmpty(finding)) return false;
            foreach (string marker in markers ?? Enumerable.Empty<string>())
            {
                if (!String.IsNullOrWhiteSpace(marker) && finding.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (string pattern in protectedPaths ?? Enumerable.Empty<string>())
            {
                string literal = LongestLiteral(pattern);
                if (literal.Length >= 3 && finding.Contains(literal, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Settle a Challenger's output against the numbered findings.
        /// </summary>
        /// <param name="challengerOutput">Challenger agent output.</param>
        /// <param name="findings">The numbered findings the Challenger was given.</param>
        /// <param name="refuteMinimumConfidence">Confidence below which a REFUTE counts as UPHOLD.</param>
        /// <param name="unchallengeable">Whether a finding always stands.</param>
        /// <returns>The settled result; <see cref="JudgeChallengeResult.Error"/> names why it cannot be used.</returns>
        public static JudgeChallengeResult Settle(
            string? challengerOutput,
            IReadOnlyList<string> findings,
            double refuteMinimumConfidence,
            Func<string, bool> unchallengeable)
        {
            if (findings == null) throw new ArgumentNullException(nameof(findings));
            if (unchallengeable == null) throw new ArgumentNullException(nameof(unchallengeable));

            JudgeChallengeResult result = new JudgeChallengeResult();
            string? value = null;
            foreach (ProgressParser.ProgressSignal signal in ProgressParser.ParseAll(challengerOutput))
            {
                if (String.Equals(signal.Type, SignalType, StringComparison.OrdinalIgnoreCase)) value = signal.Value;
            }
            if (value == null)
            {
                result.Error = "no_challenge_marker: the Challenger output holds no [ARMADA:" + SignalType.ToUpperInvariant() + "] line";
                return result;
            }

            JudgeChallengeMarker? marker;
            try
            {
                marker = JsonSerializer.Deserialize<JudgeChallengeMarker>(value, _JsonOptions);
            }
            catch (JsonException ex)
            {
                result.Error = "invalid_challenge_json: " + ex.Message;
                return result;
            }
            if (marker?.Findings == null || marker.Findings.Count == 0)
            {
                result.Error = "empty_challenge: the ruling line names no findings";
                return result;
            }

            Dictionary<int, JudgeChallengeRuling> byIndex = new Dictionary<int, JudgeChallengeRuling>();
            foreach (JudgeChallengeRuling ruling in marker.Findings)
            {
                if (ruling == null) continue;
                if (ruling.Index < 1 || ruling.Index > findings.Count)
                {
                    result.Error = "finding_out_of_range: ruling for finding " + ruling.Index + " but " + findings.Count + " were given";
                    return result;
                }
                if (byIndex.ContainsKey(ruling.Index))
                {
                    result.Error = "duplicate_ruling: finding " + ruling.Index + " was ruled more than once";
                    return result;
                }
                if (!IsRuling(ruling.Ruling, "UPHOLD") && !IsRuling(ruling.Ruling, "REFUTE"))
                {
                    result.Error = "unknown_ruling: finding " + ruling.Index + " has ruling '" + (ruling.Ruling ?? "") + "'";
                    return result;
                }
                byIndex[ruling.Index] = ruling;
            }

            for (int index = 1; index <= findings.Count; index++)
            {
                if (!byIndex.TryGetValue(index, out JudgeChallengeRuling? ruling))
                {
                    result.Error = "missing_ruling: finding " + index + " was not ruled";
                    return result;
                }

                if (!IsRuling(ruling.Ruling, "REFUTE"))
                {
                    result.Upheld.Add(index);
                }
                else if (unchallengeable(findings[index - 1]))
                {
                    result.Upheld.Add(index);
                    result.UpheldUnchallengeable.Add(index);
                }
                else if (ruling.Confidence < refuteMinimumConfidence || String.IsNullOrWhiteSpace(ruling.Evidence))
                {
                    result.Upheld.Add(index);
                    result.UpheldBelowFloor.Add(index);
                }
                else
                {
                    result.Refuted.Add(ruling);
                }
            }

            return result;
        }

        /// <summary>
        /// Compose the Challenger brief.
        /// </summary>
        /// <param name="judge">Failed Judge mission.</param>
        /// <param name="judgeOutput">Complete Judge output.</param>
        /// <param name="findings">Numbered findings.</param>
        /// <param name="baseBranch">Branch the reviewed change is measured against.</param>
        /// <returns>Brief text.</returns>
        public static string BuildBrief(Mission judge, string judgeOutput, IReadOnlyList<string> findings, string baseBranch)
        {
            if (judge == null) throw new ArgumentNullException(nameof(judge));
            if (findings == null) throw new ArgumentNullException(nameof(findings));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("A Judge returned NEEDS_REVISION on the work below. Rule UPHOLD or REFUTE on each numbered finding.");
            sb.AppendLine();
            sb.AppendLine("Reviewed commit: your dock is checked out at it. The change under review is "
                + "`git diff origin/" + baseBranch + "...HEAD`; read it and the files each finding cites.");
            sb.AppendLine("Judge mission: " + judge.Id);
            sb.AppendLine();
            sb.AppendLine("## Findings to rule on");
            for (int index = 0; index < findings.Count; index++)
                sb.AppendLine((index + 1) + ". " + findings[index]);
            sb.AppendLine();
            sb.AppendLine("## The Judge's complete output");
            sb.AppendLine(Bound(judgeOutput, MaxJudgeOutputChars));
            sb.AppendLine();
            sb.AppendLine("## The reviewed stage's brief (criteria, non-goals and owner rulings)");
            sb.AppendLine(Bound(MissionService.StripPersonaPreambles(judge.Description ?? String.Empty), MaxReviewedBriefChars));
            return sb.ToString();
        }

        #endregion

        #region Private-Methods

        private static bool IsRuling(string? value, string expected)
        {
            return String.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string LongestLiteral(string? pattern)
        {
            if (String.IsNullOrWhiteSpace(pattern)) return String.Empty;
            string best = String.Empty;
            foreach (string part in pattern.Trim().Split('*'))
            {
                string literal = part.Trim('/', '\\');
                if (literal.Length > best.Length) best = literal;
            }
            return best;
        }

        private static string Bound(string? text, int maxChars)
        {
            string value = text ?? String.Empty;
            if (value.Length <= maxChars) return value;
            return value.Substring(0, maxChars) + "\n[truncated at " + maxChars + " characters]";
        }

        #endregion
    }
}
