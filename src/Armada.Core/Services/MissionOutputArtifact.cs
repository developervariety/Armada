namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds stable, digest-backed pages from the safe redacted projection of persisted mission output.
    /// Every page names the digest and length of the joined content, plus the digest of its own content. A
    /// finished, successful stage whose report lacks a section its persona's contract requires is reported
    /// as not complete with the reason <c>report_contract_missing</c> and the missing sections named.
    /// </summary>
    public static class MissionOutputArtifact
    {
        public const int DefaultPageLength = 16000;
        public const int MaximumPageLength = 64000;
        public const string StreamTruncationMarker = "[ARMADA: streamed output truncated to retain tail]";

        public static MissionOutputArtifactPage Build(Mission mission, int offset = 0, int length = DefaultPageLength)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            // The current run is what the report-contract and verdict checks measure. Earlier runs that an
            // in-place re-run preserved are shown before it, so an operator reads every run of a mission
            // through this one read path and a re-run never erases the first run's verdict or revisions.
            string current = RuntimeLogFormatter.RedactSecrets(mission.AgentOutput);
            string preserved = RuntimeLogFormatter.RedactSecrets(mission.PreservedRunOutput);
            string output = preserved.Length == 0
                ? current
                : current.Length == 0 ? preserved : preserved + Environment.NewLine + Environment.NewLine + current;
            if (offset < 0 || offset > output.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            int safeOffset = offset;
            int safeLength = Math.Min(length, MaximumPageLength);
            if (safeOffset > 0 && safeOffset < output.Length && Char.IsLowSurrogate(output[safeOffset]))
            {
                safeOffset--;
            }

            int pageEnd = Math.Min(output.Length, safeOffset + safeLength);
            if (pageEnd < output.Length && pageEnd > safeOffset && Char.IsHighSurrogate(output[pageEnd - 1]))
            {
                if (pageEnd - safeOffset == 1) pageEnd++;
                else pageEnd--;
            }

            int pageLength = pageEnd - safeOffset;
            bool finalized = IsTerminal(mission.Status);
            bool captureTruncated = String.Equals(current, StreamTruncationMarker, StringComparison.Ordinal)
                || (current.StartsWith(StreamTruncationMarker, StringComparison.Ordinal)
                    && current.Length > StreamTruncationMarker.Length
                    && (current[StreamTruncationMarker.Length] == '\n' || current[StreamTruncationMarker.Length] == '\r'));
            string? reason = captureTruncated
                ? "stream_capture_limit"
                : mission.AgentOutput == null
                    ? (finalized ? "output_unavailable" : "mission_not_finalized")
                    : finalized ? null : "mission_not_finalized";
            List<string> missingSections = MissingReportSections(mission, current, finalized && reason == null);
            if (missingSections.Count > 0) reason = "report_contract_missing";
            bool hasMore = safeOffset + pageLength < output.Length;
            string content = pageLength == 0 ? String.Empty : output.Substring(safeOffset, pageLength);

            return new MissionOutputArtifactPage
            {
                MissionId = mission.Id,
                ArtifactRef = "mission-output:" + mission.Id,
                Offset = safeOffset,
                Length = pageLength,
                TotalLength = output.Length,
                TotalUtf8Bytes = Encoding.UTF8.GetByteCount(output),
                Content = content,
                HasMore = hasMore,
                NextOffset = hasMore ? pageEnd : null,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant(),
                PageSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                PageUtf8Bytes = Encoding.UTF8.GetByteCount(content),
                Finalized = finalized,
                Complete = finalized && mission.AgentOutput != null && !captureTruncated && missingSections.Count == 0,
                TruncationReason = reason,
                MissingReportSections = missingSections
            };
        }

        /// <summary>
        /// The report sections a finished stage's output lacks. A Judge whose verdict is PASS is held to
        /// the same section rule its verdict check applies (<see cref="JudgeReviewSections.Missing"/>)
        /// whatever its final status, so a PASS refused for a missing section reads the same gap here; a
        /// FAIL or NEEDS_REVISION review is not checked for sections, there or here. Other personas are
        /// held to their template's sections once they finish successfully.
        /// </summary>
        private static List<string> MissingReportSections(Mission mission, string output, bool readable)
        {
            if (!readable) return new List<string>();
            if (String.Equals(PersonaCatalog.NormalizeName(mission.Persona), PersonaCatalog.Judge, StringComparison.OrdinalIgnoreCase))
            {
                return MissionService.IsJudgePass(output)
                    ? JudgeReviewSections.Missing(output, mission.IsReadOnlyMode, mission.Description ?? String.Empty)
                    : new List<string>();
            }
            return IsSuccessful(mission.Status)
                ? StageReportContract.MissingSections(mission.Persona, output)
                : new List<string>();
        }

        private static bool IsSuccessful(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.WorkProduced
                || status == MissionStatusEnum.PullRequestOpen;
        }

        private static bool IsTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.Cancelled
                || status == MissionStatusEnum.WorkProduced
                || status == MissionStatusEnum.LandingFailed
                || status == MissionStatusEnum.PullRequestOpen;
        }
    }
}
