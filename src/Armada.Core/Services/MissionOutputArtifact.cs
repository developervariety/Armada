namespace Armada.Core.Services
{
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds stable, digest-backed pages from the safe redacted projection of persisted mission output.
    /// </summary>
    public static class MissionOutputArtifact
    {
        public const int DefaultPageLength = 16000;
        public const int MaximumPageLength = 64000;
        public const string StreamTruncationMarker = "[ARMADA: streamed output truncated to retain tail]";

        public static MissionOutputArtifactPage Build(Mission mission, int offset = 0, int length = DefaultPageLength)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            string output = RuntimeLogFormatter.RedactSecrets(mission.AgentOutput);
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
            bool captureTruncated = String.Equals(output, StreamTruncationMarker, StringComparison.Ordinal)
                || (output.StartsWith(StreamTruncationMarker, StringComparison.Ordinal)
                    && output.Length > StreamTruncationMarker.Length
                    && (output[StreamTruncationMarker.Length] == '\n' || output[StreamTruncationMarker.Length] == '\r'));
            string? reason = captureTruncated
                ? "stream_capture_limit"
                : mission.AgentOutput == null
                    ? (finalized ? "output_unavailable" : "mission_not_finalized")
                    : finalized ? null : "mission_not_finalized";
            bool hasMore = safeOffset + pageLength < output.Length;

            return new MissionOutputArtifactPage
            {
                MissionId = mission.Id,
                ArtifactRef = "mission-output:" + mission.Id,
                Offset = safeOffset,
                Length = pageLength,
                TotalLength = output.Length,
                TotalUtf8Bytes = Encoding.UTF8.GetByteCount(output),
                Content = pageLength == 0 ? String.Empty : output.Substring(safeOffset, pageLength),
                HasMore = hasMore,
                NextOffset = hasMore ? pageEnd : null,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant(),
                Finalized = finalized,
                Complete = finalized && mission.AgentOutput != null && !captureTruncated,
                TruncationReason = reason
            };
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
