namespace Armada.Core.Memory
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Parses MemoryConsolidator/reflection AgentOutput: exactly one <c>reflections-candidate</c> and one
    /// <c>reflections-diff</c> fenced region. Nested triple-backtick regions inside those fences match using a
    /// CommonMark-style tick-run stack so inner code fences do not prematurely end extraction. No I/O.
    /// </summary>
    public sealed class ReflectionOutputParser : IReflectionOutputParser
    {
        private const string CandidateFence = "reflections-candidate";
        private const string DiffFence = "reflections-diff";

        private static readonly HashSet<string> _ValidEvidenceConfidence =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "high",
                "mixed",
                "low",
            };

        /// <summary>Parses reflection AgentOutput markdown.</summary>
        public ReflectionOutputParseResult Parse(string agentOutput)
        {
            ReflectionOutputParseResult result = new ReflectionOutputParseResult();
            List<ReflectionOutputParseError> errors = new List<ReflectionOutputParseError>();

            if (string.IsNullOrWhiteSpace(agentOutput))
            {
                Fail(result, errors, "empty_output", "AgentOutput was null or whitespace");
                return result;
            }

            List<string> lines = SplitLinesPreserveSeparators(agentOutput);
            string? candidate = null;
            string? diffBody = null;

            int idx = 0;
            while (idx < lines.Count)
            {
                if (!TryGetFenceDelimiter(lines[idx], out int _, out bool lineCloseOnly, out string infoTail))
                {
                    idx++;
                    continue;
                }

                if (lineCloseOnly)
                {
                    idx++;
                    continue;
                }

                string? normalized = NormalizeFenceName(ExtractFirstToken(infoTail));

                if (normalized == CandidateFence || normalized == DiffFence)
                {
                    int closingIdx = FindMatchingFenceClose(lines, idx);
                    if (closingIdx < 0)
                    {
                        Fail(result, errors, "unterminated_fence",
                            "Fence " + normalized + " is not closed before end of output");
                        return result;
                    }

                    StringBuilder body = new StringBuilder();
                    for (int b = idx + 1; b < closingIdx; b++)
                    {
                        body.Append(lines[b]);
                    }

                    string content = body.ToString();
                    if (normalized == CandidateFence)
                    {
                        if (candidate != null)
                        {
                            Fail(result, errors, "duplicate_fence",
                                "More than one fenced block named reflections-candidate");
                            return result;
                        }

                        candidate = content;
                    }
                    else
                    {
                        if (diffBody != null)
                        {
                            Fail(result, errors, "duplicate_fence",
                                "More than one fenced block named reflections-diff");
                            return result;
                        }

                        diffBody = content;
                    }

                    idx = closingIdx + 1;
                    continue;
                }

                int genericClose = FindMatchingFenceClose(lines, idx);
                if (genericClose < 0)
                {
                    Fail(result, errors, "unterminated_fence",
                        "Unterminated generic fenced block starting at line containing ```");
                    return result;
                }

                idx = genericClose + 1;
            }

            if (candidate == null)
            {
                Fail(result, errors, "missing_fence", "Missing fenced block reflections-candidate");
                return result;
            }

            if (diffBody == null)
            {
                Fail(result, errors, "missing_fence", "Missing fenced block reflections-diff");
                return result;
            }

            if (!ValidateEvidenceConfidence(diffBody, errors))
            {
                FailEvidence(result, errors);
                return result;
            }

            result.Verdict = ReflectionOutputParseVerdict.Success;
            result.CandidateMarkdown = candidate;
            result.ReflectionsDiffText = diffBody;
            result.Errors.Clear();
            return result;
        }

        private static void Fail(ReflectionOutputParseResult result, List<ReflectionOutputParseError> errors, string type, string message)
        {
            result.Verdict = ReflectionOutputParseVerdict.OutputContractViolation;
            errors.Add(new ReflectionOutputParseError(type, message));
            result.Errors = errors;
            result.CandidateMarkdown = "";
            result.ReflectionsDiffText = "";
        }

        private static void FailEvidence(ReflectionOutputParseResult result, List<ReflectionOutputParseError> errors)
        {
            result.Verdict = ReflectionOutputParseVerdict.OutputContractViolation;
            result.Errors = errors;
            result.CandidateMarkdown = "";
            result.ReflectionsDiffText = "";
        }

        /// <summary>Splits into lines but keeps original line ending characters in each element (CRLF, LF, CR).</summary>
        private static List<string> SplitLinesPreserveSeparators(string text)
        {
            List<string> lines = new List<string>();
            StringBuilder current = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\r')
                {
                    current.Append(ch);
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        current.Append('\n');
                        i++;
                    }

                    lines.Add(current.ToString());
                    current.Clear();
                }
                else if (ch == '\n')
                {
                    current.Append(ch);
                    lines.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }

            return lines;
        }

        private static string TrimUpToThreeSpaces(string lineIncludingNewline)
        {
            string noTrailingBreak = lineIncludingNewline.TrimEnd('\r', '\n');
            int indent = 0;
            while (indent < noTrailingBreak.Length && indent < 3 && noTrailingBreak[indent] == ' ')
            {
                indent++;
            }

            return noTrailingBreak.Substring(indent);
        }

        /// <summary>Detect a CommonMark fence line (at least three backticks) after optional indentation.</summary>
        private static bool TryGetFenceDelimiter(string rawLine, out int tickRun, out bool isClosingOnlyDelimiter, out string infoAfterTicksTrimmed)
        {
            string logical = TrimUpToThreeSpaces(rawLine).TrimEnd(' ', '\t');
            tickRun = 0;
            while (tickRun < logical.Length && logical[tickRun] == '`')
            {
                tickRun++;
            }

            if (tickRun < 3)
            {
                isClosingOnlyDelimiter = false;
                infoAfterTicksTrimmed = "";
                return false;
            }

            string tail = tickRun < logical.Length ? logical.Substring(tickRun) : "";
            infoAfterTicksTrimmed = tail.Trim();
            isClosingOnlyDelimiter = infoAfterTicksTrimmed.Length == 0;
            return true;
        }

        /// <summary>Find closing line for fenced block opened on <paramref name="openingLineIndex"/> using nested stacks.</summary>
        private static int FindMatchingFenceClose(List<string> lines, int openingLineIndex)
        {
            bool okOpen = TryGetFenceDelimiter(lines[openingLineIndex], out int outerTicks, out bool closeOnlyOpening, out string _);
            if (!okOpen || closeOnlyOpening || outerTicks < 3)
            {
                return -1;
            }

            Stack<int> stack = new Stack<int>();
            stack.Push(outerTicks);

            for (int i = openingLineIndex + 1; i < lines.Count; i++)
            {
                if (!TryGetFenceDelimiter(lines[i], out int tickRun, out bool lineCloseOnly, out string _))
                    continue;

                if (tickRun < 3)
                    continue;

                if (lineCloseOnly)
                {
                    if (stack.Count > 0 && tickRun >= stack.Peek())
                    {
                        stack.Pop();
                        if (stack.Count == 0)
                        {
                            return i;
                        }
                    }
                }
                else
                {
                    stack.Push(tickRun);
                }
            }

            return -1;
        }

        private static string ExtractFirstToken(string tail)
        {
            if (string.IsNullOrEmpty(tail))
            {
                return "";
            }

            int end = 0;
            while (end < tail.Length && !char.IsWhiteSpace(tail[end]))
                end++;

            return tail.Substring(0, end);
        }

        private static string? NormalizeFenceName(string openerName)
        {
            if (string.IsNullOrEmpty(openerName))
            {
                return null;
            }

            return openerName.ToLowerInvariant();
        }

        /// <summary>
        /// When the diff body parses as JSON and exposes evidenceConfidence, enforce high|mixed|low (case insensitive).
        /// </summary>
        private static bool ValidateEvidenceConfidence(string diffBody, List<ReflectionOutputParseError> errors)
        {
            string trimmed = diffBody.Trim();
            if (trimmed.Length == 0)
            {
                return true;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                return true;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return true;
                }

                if (!TryGetEvidenceConfidence(doc.RootElement, out JsonElement ec))
                {
                    return true;
                }

                if (ec.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new ReflectionOutputParseError("invalid_evidence_confidence",
                        "evidenceConfidence must be a string"));
                    return false;
                }

                string? raw = ec.GetString();
                string normalized = (raw ?? "").Trim().ToLowerInvariant();
                if (!_ValidEvidenceConfidence.Contains(normalized))
                {
                    errors.Add(new ReflectionOutputParseError("invalid_evidence_confidence",
                        "evidenceConfidence must be one of high, mixed, low"));
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetEvidenceConfidence(JsonElement root, out JsonElement ec)
        {
            if (root.TryGetProperty("evidenceConfidence", out ec))
            {
                return true;
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "evidenceConfidence", StringComparison.OrdinalIgnoreCase))
                {
                    ec = property.Value;
                    return true;
                }
            }

            ec = default;
            return false;
        }
    }
}
