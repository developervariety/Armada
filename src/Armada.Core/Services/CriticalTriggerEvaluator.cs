namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Critical-trigger evaluator: any of path/content/convention-failure/size
    /// firing escalates the auto-landed entry to deep review.
    /// </summary>
    public sealed class CriticalTriggerEvaluator : ICriticalTriggerEvaluator
    {
        private static readonly string[] _PathKeywords = new string[]
        {
            "auth", "token", "crypto", "rsa", "tenant", "rls", "hmac", "paseto", "keystore", "security"
        };

        private static readonly Regex[] _ContentPatterns = new Regex[]
        {
            new Regex(@"HasQueryFilter", RegexOptions.Compiled),
            new Regex(@"BypassTenantFilter", RegexOptions.Compiled),
            new Regex(@"\[Authorize", RegexOptions.Compiled),
            new Regex(@"\bunsafe\b", RegexOptions.Compiled),
            new Regex(@"catch\s*\(\s*Exception\b[^)]*\)\s*\{\s*\}", RegexOptions.Compiled),
            new Regex(@"Assembly\.Load", RegexOptions.Compiled),
            new Regex(@"Process\.Start", RegexOptions.Compiled),
        };

        private const int SizeThresholdAddedLines = 50;
        private const int SizeThresholdFiles = 3;

        /// <summary>
        /// Evaluate the diff and convention result, then merge in any additional escalation criteria
        /// supplied by a caller — for example the D17 <c>change_substance</c> typed decision's
        /// <c>risky</c> reason, which fires when the added hunks touch a safety step, a guard, or a wire
        /// byte. Additional criteria are additive only: they can escalate an entry to deep review, never
        /// clear a criterion the deterministic evaluator already fired.
        /// </summary>
        /// <param name="unifiedDiff">The unified diff of the change.</param>
        /// <param name="conventionResult">The convention-check result.</param>
        /// <param name="additionalCriteria">Extra escalation criteria to merge, or null.</param>
        /// <returns>The fired criteria, including any additional ones.</returns>
        public CriticalTriggerResult Evaluate(string unifiedDiff, ConventionCheckResult conventionResult, IEnumerable<string>? additionalCriteria)
        {
            CriticalTriggerResult result = Evaluate(unifiedDiff, conventionResult);
            if (additionalCriteria != null)
            {
                foreach (string criterion in additionalCriteria)
                {
                    if (String.IsNullOrWhiteSpace(criterion)) continue;
                    if (!result.TriggeredCriteria.Contains(criterion)) result.TriggeredCriteria.Add(criterion);
                }
            }
            result.Fired = result.TriggeredCriteria.Count > 0;
            return result;
        }

        /// <summary>Evaluates the diff and convention result, returning fired criteria.</summary>
        public CriticalTriggerResult Evaluate(string unifiedDiff, ConventionCheckResult conventionResult)
        {
            if (conventionResult == null) throw new ArgumentNullException(nameof(conventionResult));

            CriticalTriggerResult result = new CriticalTriggerResult();
            if (string.IsNullOrEmpty(unifiedDiff)) return result;

            ParseDiff(unifiedDiff, out HashSet<string> paths, out int fileCount, out int addedLines);

            // Path patterns
            foreach (string path in paths)
            {
                string pathLower = path.ToLowerInvariant();
                foreach (string keyword in _PathKeywords)
                {
                    if (pathLower.Contains(keyword))
                    {
                        result.TriggeredCriteria.Add("path");
                        goto pathDone;
                    }
                }
            }
            pathDone:

            // Content patterns
            foreach (Regex pattern in _ContentPatterns)
            {
                if (pattern.IsMatch(unifiedDiff))
                {
                    result.TriggeredCriteria.Add("content");
                    break;
                }
            }

            // Convention failure
            if (!conventionResult.Passed)
            {
                result.TriggeredCriteria.Add("convention");
            }

            // Diff size threshold
            if (addedLines > SizeThresholdAddedLines || fileCount > SizeThresholdFiles)
            {
                result.TriggeredCriteria.Add("size");
            }

            result.Fired = result.TriggeredCriteria.Count > 0;
            return result;
        }

        private static void ParseDiff(string diff, out HashSet<string> paths, out int fileCount, out int addedLines)
        {
            // Paths come from the shared diff reader: a deletion counts under its old path and a
            // rename under both, so a deleted or moved sensitive file still fires the path trigger.
            paths = new HashSet<string>(StringComparer.Ordinal);
            addedLines = 0;

            HashSet<string> files = new HashSet<string>(StringComparer.Ordinal);
            foreach (GitDiffFileChange change in GitDiffPaths.ParseFiles(diff))
            {
                files.Add(change.NewPath ?? change.OldPath ?? String.Empty);
                foreach (string path in GitDiffPaths.PathsOf(change)) paths.Add(path);
            }

            fileCount = files.Count;

            foreach (string rawLine in diff.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length > 0 && line[0] == '+'
                    && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    addedLines++;
                }
            }
        }
    }
}
