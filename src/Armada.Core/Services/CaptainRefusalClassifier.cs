namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Classifies whether a captain refused its mission. The structured marker is the typed signal and wins
    /// whenever it is present. Output recognition is the fallback for captains that decline in prose, and
    /// it never overrides a captain that claimed completion.
    /// </summary>
    public static class CaptainRefusalClassifier
    {
        #region Public-Members

        /// <summary>Structured marker a captain writes when it declines work, followed by its reason.</summary>
        public const string RefusalMarker = "[ARMADA:RESULT] REFUSED";

        /// <summary>Maximum characters kept from a refusal reason or evidence line.</summary>
        public const int MaxReasonChars = 500;

        #endregion

        #region Private-Members

        // How many trailing output lines are inspected. A refusal is the captain's closing statement; a
        // phrase deep inside a long narration is quoted material, not a refusal.
        private const int _TrailingLines = 40;

        // Prose a model uses to decline. Matched lower-case, only in the trailing lines, and only when no
        // completion marker or verdict was written.
        private static readonly string[] _RefusalPhrases =
        {
            "i can't help with",
            "i cannot help with",
            "i can't assist with",
            "i cannot assist with",
            "i won't help with",
            "i will not help with",
            "i'm not able to help with",
            "i am not able to help with",
            "i must decline",
            "i can't comply",
            "i cannot comply"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Classify a captain's output.
        /// </summary>
        /// <param name="agentOutput">Captured agent output; may be null.</param>
        /// <returns>The classified refusal; <see cref="CaptainRefusalKindEnum.None"/> when the captain did not refuse.</returns>
        public static CaptainRefusal Classify(string? agentOutput)
        {
            CaptainRefusal none = new CaptainRefusal();
            if (String.IsNullOrWhiteSpace(agentOutput)) return none;

            string[] lines = agentOutput.Replace("\r\n", "\n").Split('\n');

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                int markerIndex = line.IndexOf(RefusalMarker, StringComparison.Ordinal);
                if (markerIndex < 0) continue;

                string reason = line.Substring(markerIndex + RefusalMarker.Length).TrimStart(':', ' ', '-').Trim();
                return new CaptainRefusal
                {
                    Kind = CaptainRefusalKindEnum.DeclaredRefusal,
                    Reason = Bound(reason),
                    Evidence = Bound(line)
                };
            }

            if (agentOutput.Contains(MissionService.CompletionMarker, StringComparison.Ordinal)
                || agentOutput.Contains(MissionService.VerdictMarker, StringComparison.Ordinal))
            {
                return none;
            }

            int first = Math.Max(0, lines.Length - _TrailingLines);
            for (int i = lines.Length - 1; i >= first; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                if (ProviderQuotaLimitDetector.IsProviderSafeguardBlockSignal(line))
                {
                    return new CaptainRefusal
                    {
                        Kind = CaptainRefusalKindEnum.ProviderSafeguardBlock,
                        Reason = Bound(line),
                        Evidence = Bound(line)
                    };
                }

                string lower = line.ToLowerInvariant().Replace('’', '\'');
                foreach (string phrase in _RefusalPhrases)
                {
                    if (!lower.Contains(phrase, StringComparison.Ordinal)) continue;
                    return new CaptainRefusal
                    {
                        Kind = CaptainRefusalKindEnum.ModelPolicyRefusal,
                        Reason = Bound(line),
                        Evidence = Bound(line)
                    };
                }
            }

            return none;
        }

        #endregion

        #region Private-Methods

        private static string Bound(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return value.Length <= MaxReasonChars ? value : value.Substring(0, MaxReasonChars) + "...";
        }

        #endregion
    }
}
