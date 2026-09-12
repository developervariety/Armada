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
            string inlineBody = String.Empty;
            for (int i = 0; i < lines.Length; i++)
            {
                if (TryParseFollowUpLabel(lines[i], out inlineBody))
                {
                    result.Present = true;
                    startIndex = i + 1;
                    break;
                }
            }
            if (startIndex < 0) return result;

            System.Text.StringBuilder body = new System.Text.StringBuilder();
            if (!String.IsNullOrWhiteSpace(inlineBody)) body.AppendLine(inlineBody);
            for (int i = startIndex; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("## ", StringComparison.Ordinal)
                    || trimmed.StartsWith("[ARMADA:", StringComparison.OrdinalIgnoreCase)) break;
                body.Append(lines[i]);
                body.Append('\n');
            }

            string text = body.ToString().Trim();
            result.ExplicitNone = text.Equals("(none)", StringComparison.OrdinalIgnoreCase);
            result.Body = String.IsNullOrEmpty(text) || result.ExplicitNone ? null : text;
            return result;
        }

        private static bool TryParseFollowUpLabel(string line, out string inlineBody)
        {
            inlineBody = String.Empty;
            string text = line.Trim();
            if (text.StartsWith("- ", StringComparison.Ordinal)) text = text.Substring(2).TrimStart();

            bool markdownHeading = text.StartsWith("## ", StringComparison.Ordinal);
            if (markdownHeading) text = text.Substring(3).Trim();

            bool bold = text.StartsWith("**", StringComparison.Ordinal);
            if (bold)
            {
                int closing = text.IndexOf("**", 2, StringComparison.Ordinal);
                if (closing < 0) return false;
                string label = text.Substring(2, closing - 2).Trim();
                string afterBold = text.Substring(closing + 2).Trim();
                if (!TryParseLabelText(label, !markdownHeading, out string labelTail)) return false;
                inlineBody = JoinInlineParts(labelTail, afterBold.TrimStart(':').Trim());
                return true;
            }

            return TryParseLabelText(text, !markdownHeading, out inlineBody);
        }

        private static bool TryParseLabelText(string text, bool requireColon, out string inlineBody)
        {
            inlineBody = String.Empty;
            string[] prefixes = { "Suggested Follow-up", "Recommended Follow-up", "Tracked Follow-up" };
            string? prefix = null;
            foreach (string candidate in prefixes)
            {
                if (text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    prefix = candidate;
                    break;
                }
            }
            if (prefix == null) return false;

            int offset = prefix.Length;
            if (offset < text.Length && (text[offset] == 's' || text[offset] == 'S')) offset++;
            string remainder = text.Substring(offset).TrimStart();

            const string orchestratorQualifier = "for the orchestrator";
            if (remainder.StartsWith(orchestratorQualifier, StringComparison.OrdinalIgnoreCase))
                remainder = remainder.Substring(orchestratorQualifier.Length).TrimStart();

            if (remainder.StartsWith("(", StringComparison.Ordinal))
            {
                int closing = remainder.IndexOf(')');
                if (closing < 0) return false;
                remainder = remainder.Substring(closing + 1).TrimStart();
            }

            if (remainder.StartsWith(":", StringComparison.Ordinal))
            {
                inlineBody = remainder.Substring(1).Trim();
                return true;
            }

            return !requireColon && remainder.Length == 0;
        }

        private static string JoinInlineParts(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first)) return second;
            if (String.IsNullOrWhiteSpace(second)) return first;
            return first + " " + second;
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
