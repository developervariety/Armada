namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>Reads explicit finding scores without changing the original review.</summary>
    public static class JudgeFindingTriage
    {
        /// <summary>Durable refusal prefix for a malformed scored review.</summary>
        public const string InvalidReason = "judge_finding_format_invalid:";
        /// <summary>Persisted marker that the one finding-format correction was used.</summary>
        public const string CorrectionHeading = "## Judge Finding Format Correction";
        /// <summary>Durable applied-score event type.</summary>
        public const string AppliedEvent = "judge.findings_applied";
        /// <summary>Instruction shared by Judge prompts and the one format re-ask.</summary>
        public const string OutputInstruction =
            "FAIL/NEEDS_REVISION: add `## Findings`, one `[ARMADA:FINDING]` JSON object per line. "
            + "Fields: unique id; text with supporting evidence; frequency and severity, integers 1-10; "
            + "criterion, exact unmet criterion text or null; rule, violated memory rule or null. "
            + "No prose/fences. Uncited scores below the vessel threshold are follow-ups. "
            + "Cited criteria/rules and failed required Checks block.";

        private const string _Heading = "## Findings";
        private const string _Prefix = "[ARMADA:FINDING] ";
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>Whether a persisted brief has already requested the one correction.</summary>
        public static bool HasCorrection(string? description)
            => description?.Contains(CorrectionHeading, StringComparison.Ordinal) == true;

        /// <summary>Whether this review uses the structured finding section.</summary>
        public static bool HasSection(string? output)
        {
            foreach (string line in (output ?? String.Empty).Replace("\r\n", "\n").Split('\n'))
                if (line == _Heading) return true;
            return false;
        }

        /// <summary>Classify every explicit finding; malformed input never becomes approval.</summary>
        /// <param name="output">Original complete review.</param>
        /// <param name="threshold">Vessel score threshold from one to one hundred.</param>
        /// <param name="allowEmpty">Allow an explicit (none) section on an existing PASS.</param>
        /// <returns>Blockers, follow-ups and a named format refusal.</returns>
        public static JudgeFindingTriageResult Evaluate(string? output, int threshold, bool allowEmpty = false)
        {
            JudgeFindingTriageResult result = new JudgeFindingTriageResult
            {
                Threshold = threshold,
                ReportSha256 = Hash(output),
                Error = InvalidReason + " missing Findings section"
            };
            if (threshold < 1 || threshold > 100) { result.Error = InvalidReason + " invalid vessel threshold"; return result; }
            if ((output?.Length ?? 0) > 262144) { result.Error = InvalidReason + " report exceeds finding-reader limit"; return result; }
            bool inSection = false;
            int headings = 0;
            bool explicitNone = false;
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in (output ?? String.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (line == _Heading) { headings++; inSection = true; continue; }
                if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("[ARMADA:VERDICT]", StringComparison.Ordinal)) inSection = false;
                if (!inSection || String.IsNullOrWhiteSpace(line)) continue;
                result.Error = InvalidReason + " each finding needs one scored JSON line";
                if (line == "(none)" && allowEmpty && !explicitNone) { explicitNone = true; continue; }
                if (!line.StartsWith(_Prefix, StringComparison.Ordinal)) return result;
                JudgeFinding? finding;
                try { finding = JsonSerializer.Deserialize<JudgeFinding>(line.Substring(_Prefix.Length), _Json); }
                catch (JsonException) { return result; }
                if (finding == null || String.IsNullOrWhiteSpace(finding.Id) || String.IsNullOrWhiteSpace(finding.Text)
                    || finding.Id.Length > 100 || finding.Text.Length > 12000
                    || !finding.Frequency.HasValue || finding.Frequency.Value < 1 || finding.Frequency.Value > 10
                    || !finding.Severity.HasValue || finding.Severity.Value < 1 || finding.Severity.Value > 10
                    || !ids.Add(finding.Id) || ids.Count > 50) return result;
                bool cited = !String.IsNullOrWhiteSpace(finding.Criterion) || !String.IsNullOrWhiteSpace(finding.Rule);
                if (cited || finding.Frequency.Value * finding.Severity.Value >= threshold) result.Blocking.Add(finding);
                else result.FollowUps.Add(finding);
            }
            result.Error = headings == 1 && ((ids.Count > 0 && !explicitNone) || (ids.Count == 0 && explicitNone)) ? null : InvalidReason + " exactly one nonempty Findings section is required";
            return result;
        }

        /// <summary>Format the classified subset for retained follow-ups or rescue feedback.</summary>
        public static string Format(IEnumerable<JudgeFinding> findings)
        {
            StringBuilder text = new StringBuilder();
            foreach (JudgeFinding finding in findings)
            {
                text.Append("- ").Append(finding.Id).Append(": ").Append(finding.Text)
                    .Append(" (frequency ").Append(finding.Frequency).Append(", severity ").Append(finding.Severity).Append(')');
                if (!String.IsNullOrWhiteSpace(finding.Criterion)) text.Append("; criterion: ").Append(finding.Criterion);
                if (!String.IsNullOrWhiteSpace(finding.Rule)) text.Append("; rule: ").Append(finding.Rule);
                text.AppendLine();
            }
            return text.ToString().TrimEnd();
        }

        /// <summary>Read a stored applied decision only for the identical original report.</summary>
        public static JudgeFindingTriageResult? ReadApplied(string? payload, string? report)
        {
            if (String.IsNullOrWhiteSpace(payload)) return null;
            JudgeFindingTriageResult? result = JsonSerializer.Deserialize<JudgeFindingTriageResult>(payload, _Json);
            return result?.ReportSha256 == Hash(report) && result.IsValid && !String.IsNullOrEmpty(result.EffectiveVerdict) ? result : null;
        }

        /// <summary>Hash the exact retained review text.</summary>
        public static string Hash(string? output)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output ?? String.Empty))).ToLowerInvariant();
    }
}
