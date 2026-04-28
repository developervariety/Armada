namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Microsoft.Extensions.FileSystemGlobbing;

    /// <summary>
    /// Pure, stateless evaluator that parses a unified diff and applies
    /// <see cref="AutoLandPredicate"/> rules in order: Enabled, MaxFiles,
    /// MaxAddedLines, DenyPaths, AllowPaths. No I/O or DI dependencies.
    /// </summary>
    public sealed class AutoLandEvaluator : IAutoLandEvaluator
    {
        /// <inheritdoc />
        public EvaluationResult Evaluate(string unifiedDiff, AutoLandPredicate predicate)
        {
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (!predicate.Enabled) return new EvaluationResult.Fail("disabled");

            ParseDiff(unifiedDiff, out HashSet<string> paths, out int addedLines);

            if (predicate.MaxFiles is int maxFiles && paths.Count > maxFiles)
                return new EvaluationResult.Fail($"maxFiles:{paths.Count}>{maxFiles}");

            if (predicate.MaxAddedLines is int maxLines && addedLines > maxLines)
                return new EvaluationResult.Fail($"maxAddedLines:{addedLines}>{maxLines}");

            if (predicate.DenyPaths is { Count: > 0 } deny)
            {
                Matcher denyMatcher = new Matcher();
                foreach (string pattern in deny) denyMatcher.AddInclude(pattern);
                foreach (string path in paths)
                {
                    if (denyMatcher.Match(path).HasMatches)
                        return new EvaluationResult.Fail($"denyPath:{path}");
                }
            }

            if (predicate.AllowPaths is { Count: > 0 } allow)
            {
                Matcher allowMatcher = new Matcher();
                foreach (string pattern in allow) allowMatcher.AddInclude(pattern);
                foreach (string path in paths)
                {
                    if (!allowMatcher.Match(path).HasMatches)
                        return new EvaluationResult.Fail($"allowPaths:violated:{path}");
                }
            }

            return new EvaluationResult.Pass();
        }

        private static void ParseDiff(string diff, out HashSet<string> paths, out int addedLines)
        {
            paths = new HashSet<string>(StringComparer.Ordinal);
            addedLines = 0;
            if (string.IsNullOrEmpty(diff)) return;

            foreach (string rawLine in diff.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                {
                    paths.Add(line.Substring("+++ b/".Length));
                }
                else if (line.Length > 0 && line[0] == '+'
                         && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    addedLines++;
                }
            }
        }
    }
}
