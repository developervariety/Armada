namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.RegularExpressions;
    using Armada.Core.Settings;

    /// <summary>
    /// Fails a change whose ADDED lines match an operator-configured banned pattern. This is the
    /// general, domain-neutral form of an absolute diff guard: the product carries the mechanism and
    /// the deployment carries the patterns (in settings), so no domain-specific rule is hardcoded
    /// here. Only added code lines are read; a pure comment line is skipped (a guard bans the path,
    /// not the mention of it). A rule whose pattern is not a valid regex is skipped and named, so one
    /// bad rule never blocks every change or throws into the check.
    /// </summary>
    public static class BannedDiffPatternClassifier
    {
        #region Private-Members

        private static readonly Regex _HunkPath = new Regex(@"^\+\+\+ b/(.+)$", RegexOptions.Compiled);
        private static readonly TimeSpan _MatchTimeout = TimeSpan.FromMilliseconds(250);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Classify the added lines of a unified diff against the configured banned patterns.
        /// </summary>
        /// <param name="unifiedDiff">The unified diff to read.</param>
        /// <param name="rules">The configured banned-pattern rules; null or empty means no guard.</param>
        /// <returns>The result; empty findings when nothing is banned.</returns>
        public static BannedDiffPatternResult Classify(string? unifiedDiff, IReadOnlyList<BannedDiffPatternRule>? rules)
        {
            BannedDiffPatternResult result = new BannedDiffPatternResult();
            if (String.IsNullOrEmpty(unifiedDiff) || rules == null || rules.Count == 0) return result;

            List<CompiledRule> compiled = new List<CompiledRule>();
            foreach (BannedDiffPatternRule rule in rules)
            {
                if (rule == null || String.IsNullOrWhiteSpace(rule.Pattern)) continue;
                try
                {
                    compiled.Add(new CompiledRule(rule, new Regex(rule.Pattern, RegexOptions.None, _MatchTimeout)));
                }
                catch (ArgumentException)
                {
                    result.SkippedRules.Add(rule.Name + " (invalid regex)");
                }
            }
            if (compiled.Count == 0) return result;

            string path = "(unknown)";
            foreach (string raw in unifiedDiff.Replace("\r\n", "\n").Split('\n'))
            {
                Match header = _HunkPath.Match(raw);
                if (header.Success) { path = header.Groups[1].Value.Trim(); continue; }
                if (raw.StartsWith("+++", StringComparison.Ordinal) || raw.StartsWith("---", StringComparison.Ordinal)
                    || raw.StartsWith("@@", StringComparison.Ordinal) || raw.StartsWith("diff ", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!raw.StartsWith("+", StringComparison.Ordinal)) continue;

                string content = raw.Substring(1);
                string trimmed = content.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (CompiledRule rule in compiled)
                {
                    try
                    {
                        if (rule.Regex.IsMatch(content))
                            result.Findings.Add(new BannedDiffPatternFinding(rule.Rule.Name, rule.Rule.Description, path, content.Trim()));
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // A pathological pattern is treated as no match on this line rather than a throw.
                    }
                }
            }

            return result;
        }

        /// <summary>Render findings as the text a check output carries.</summary>
        /// <param name="result">The classification.</param>
        /// <returns>A human-readable block.</returns>
        public static string FormatFindings(BannedDiffPatternResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Banned-diff guard: " + result.Findings.Count + " banned pattern match(es) on added lines.");
            foreach (BannedDiffPatternFinding finding in result.Findings)
                sb.AppendLine("  [" + finding.RuleName + "] " + finding.Path + ": " + finding.Description + " -- " + Abbreviate(finding.Line));
            foreach (string skipped in result.SkippedRules)
                sb.AppendLine("  skipped rule: " + skipped);
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Private-Methods

        private static string Abbreviate(string line)
        {
            string clean = (line ?? String.Empty).Trim();
            return clean.Length <= 120 ? clean : clean.Substring(0, 117) + "...";
        }

        private sealed class CompiledRule
        {
            public CompiledRule(BannedDiffPatternRule rule, Regex regex) { Rule = rule; Regex = regex; }
            public BannedDiffPatternRule Rule { get; }
            public Regex Regex { get; }
        }

        #endregion
    }

    /// <summary>The result of a banned-diff-pattern classification.</summary>
    public sealed class BannedDiffPatternResult
    {
        /// <summary>The banned patterns matched on added lines.</summary>
        public List<BannedDiffPatternFinding> Findings { get; } = new List<BannedDiffPatternFinding>();

        /// <summary>Rules skipped because their pattern was not a valid regex.</summary>
        public List<string> SkippedRules { get; } = new List<string>();

        /// <summary>Whether the diff matched a banned pattern.</summary>
        public bool HasBanned => Findings.Count > 0;
    }

    /// <summary>One banned-pattern match on an added line.</summary>
    public sealed class BannedDiffPatternFinding
    {
        /// <summary>Create a finding.</summary>
        /// <param name="ruleName">The rule name.</param>
        /// <param name="description">The rule's reason.</param>
        /// <param name="path">The file path.</param>
        /// <param name="line">The added line text.</param>
        public BannedDiffPatternFinding(string ruleName, string description, string path, string line)
        {
            RuleName = ruleName ?? String.Empty;
            Description = description ?? String.Empty;
            Path = path ?? String.Empty;
            Line = line ?? String.Empty;
        }

        /// <summary>The rule that matched.</summary>
        public string RuleName { get; }

        /// <summary>The rule's human reason.</summary>
        public string Description { get; }

        /// <summary>The file the added line belongs to.</summary>
        public string Path { get; }

        /// <summary>The added line text.</summary>
        public string Line { get; }
    }
}
