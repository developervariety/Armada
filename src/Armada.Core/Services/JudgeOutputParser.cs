namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Parses the durable parts of Judge output that are shared by live capture and repair tools.
    /// </summary>
    public static class JudgeOutputParser
    {
        /// <summary>Parsed Suggested Follow-ups section.</summary>
        public sealed class FollowUpSection
        {
            /// <summary>True when the output contains the section heading.</summary>
            public bool Present { get; set; }

            /// <summary>True when the section contains the explicit no-work sentinel.</summary>
            public bool ExplicitNone { get; set; }

            /// <summary>Trimmed section body, or null for empty and explicit-none sections.</summary>
            public string? Body { get; set; }
        }

        /// <summary>Extract the Suggested Follow-ups section without losing explicit-none evidence.</summary>
        public static FollowUpSection ParseSuggestedFollowUps(string? agentOutput)
        {
            FollowUpSection result = new FollowUpSection();
            if (String.IsNullOrEmpty(agentOutput)) return result;

            string[] lines = agentOutput.Replace("\r\n", "\n").Split('\n');
            int startIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("## ", StringComparison.Ordinal)
                    && trimmed.Substring(3).Trim().Equals("Suggested Follow-ups", StringComparison.OrdinalIgnoreCase))
                {
                    result.Present = true;
                    startIndex = i + 1;
                    break;
                }
            }
            if (startIndex < 0) return result;

            System.Text.StringBuilder body = new System.Text.StringBuilder();
            for (int i = startIndex; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("## ", StringComparison.Ordinal)) break;
                body.Append(lines[i]);
                body.Append('\n');
            }

            string text = body.ToString().Trim();
            result.ExplicitNone = text.Equals("(none)", StringComparison.OrdinalIgnoreCase);
            result.Body = String.IsNullOrEmpty(text) || result.ExplicitNone ? null : text;
            return result;
        }

        /// <summary>Return a normalized verdict label for durable follow-up capture.</summary>
        public static string ParseVerdictLabel(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            string[] lines = (mission.AgentOutput ?? String.Empty).Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string normalized = lines[i].Trim().Trim('*', '_', '`', '#', '>', '-', ' ')
                    .Replace("[ARMADA:VERDICT]", String.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("[VERDICT]", String.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim();
                if (normalized.Equals("NEEDS_REVISION", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("NEEDS REVISION", StringComparison.OrdinalIgnoreCase)) return "NEEDS_REVISION";
                if (normalized.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) return "FAIL";
                if (normalized.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                    return mission.Status == MissionStatusEnum.Failed ? "FAIL" : "PASS";
            }

            if ((mission.FailureReason ?? String.Empty).Contains("NEEDS_REVISION", StringComparison.OrdinalIgnoreCase))
                return "NEEDS_REVISION";
            return mission.Status == MissionStatusEnum.Complete ? "PASS" : "FAIL";
        }
    }
}
