namespace Armada.Core.Recovery
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Pure implementation of <see cref="IMergeFailureClassifier"/>: maps a
    /// <see cref="MergeFailureContext"/> captured at fail-time to a structured
    /// <see cref="MergeFailureClassification"/>. Side-effect free.
    /// </summary>
    public sealed class MergeFailureClassifier : IMergeFailureClassifier
    {
        private const int _SUMMARY_MAX_LENGTH = 200;

        /// <inheritdoc />
        public MergeFailureClassification Classify(MergeFailureContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            IReadOnlyList<string> conflictedFiles = DeduplicateFiles(context.ConflictedFiles);

            string gitStdout = context.GitStandardOutput ?? String.Empty;
            string gitStderr = context.GitStandardError ?? String.Empty;
            string gitCombined = gitStdout + "\n" + gitStderr;

            bool gitMergeAttempted = context.GitExitCode.HasValue;
            bool gitMergeSucceeded = gitMergeAttempted && context.GitExitCode!.Value == 0;
            bool testRan = context.TestExitCode.HasValue;
            bool testFailed = testRan && context.TestExitCode!.Value != 0;

            // Test failure AFTER successful merge.
            if (testFailed && gitMergeSucceeded)
            {
                string summary = "Tests failed after merge (exit " + context.TestExitCode!.Value + ")";
                return new MergeFailureClassification(
                    MergeFailureClassEnum.TestFailureAfterMerge,
                    Truncate(summary),
                    conflictedFiles);
            }

            // Stale-base markers come from the git output even when CONFLICT is also
            // mentioned (rare); check before generic conflict-marker detection.
            if (gitMergeAttempted && !gitMergeSucceeded && IsStaleBase(gitCombined))
            {
                string summary = "Stale base: target has advanced past captain merge-base";
                return new MergeFailureClassification(
                    MergeFailureClassEnum.StaleBase,
                    Truncate(summary),
                    conflictedFiles);
            }

            // Text conflict from git output.
            if (gitMergeAttempted && !gitMergeSucceeded && HasConflictMarker(gitCombined))
            {
                string fileSummary = conflictedFiles.Count > 0
                    ? "Text conflict in " + conflictedFiles.Count + " file" + (conflictedFiles.Count == 1 ? "" : "s")
                    : "Text conflict reported by git";
                return new MergeFailureClassification(
                    MergeFailureClassEnum.TextConflict,
                    Truncate(fileSummary),
                    conflictedFiles);
            }

            // Conflicted files reported even without explicit marker -- still a text conflict.
            if (conflictedFiles.Count > 0)
            {
                string fileSummary = "Text conflict in " + conflictedFiles.Count + " file" + (conflictedFiles.Count == 1 ? "" : "s");
                return new MergeFailureClassification(
                    MergeFailureClassEnum.TextConflict,
                    Truncate(fileSummary),
                    conflictedFiles);
            }

            // Test failed BEFORE merge attempted.
            if (testFailed && !gitMergeAttempted)
            {
                string summary = "Tests failed before merge attempted (exit " + context.TestExitCode!.Value + ")";
                return new MergeFailureClassification(
                    MergeFailureClassEnum.TestFailureBeforeMerge,
                    Truncate(summary),
                    conflictedFiles);
            }

            // Fallback.
            string unknownSummary = BuildUnknownSummary(context);
            return new MergeFailureClassification(
                MergeFailureClassEnum.Unknown,
                Truncate(unknownSummary),
                conflictedFiles);
        }

        private static bool HasConflictMarker(string gitCombined)
        {
            if (String.IsNullOrEmpty(gitCombined)) return false;
            return gitCombined.IndexOf("CONFLICT (", StringComparison.Ordinal) >= 0
                || gitCombined.IndexOf("Automatic merge failed", StringComparison.Ordinal) >= 0;
        }

        private static bool IsStaleBase(string gitCombined)
        {
            if (String.IsNullOrEmpty(gitCombined)) return false;
            if (gitCombined.IndexOf("non-fast-forward", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (gitCombined.IndexOf("unrelated histories", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (gitCombined.IndexOf("fetch first", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static IReadOnlyList<string> DeduplicateFiles(IReadOnlyList<string>? files)
        {
            if (files == null || files.Count == 0) return Array.Empty<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> result = new List<string>();
            foreach (string file in files)
            {
                if (String.IsNullOrEmpty(file)) continue;
                if (seen.Add(file)) result.Add(file);
            }
            return result;
        }

        private static string BuildUnknownSummary(MergeFailureContext context)
        {
            string gitPart = context.GitExitCode.HasValue ? "git exit=" + context.GitExitCode.Value : "no git";
            string testPart = context.TestExitCode.HasValue ? "test exit=" + context.TestExitCode.Value : "no test";
            return "Unclassified failure: " + gitPart + ", " + testPart;
        }

        private static string Truncate(string summary)
        {
            if (String.IsNullOrEmpty(summary)) return String.Empty;
            if (summary.Length <= _SUMMARY_MAX_LENGTH) return summary;
            return summary.Substring(0, _SUMMARY_MAX_LENGTH);
        }
    }
}
