namespace Armada.Core.Recovery
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Pure deterministic classifier mapping captured (git, tests,
    /// conflicted-files) snapshots to a structured <see cref="MergeFailureSignal"/>.
    /// No I/O, no database access, no logging. Behavior is fully covered by
    /// <c>MergeFailureClassifierTests</c>; the recovery router (M3) consumes
    /// the output to decide between Redispatch / RebaseCaptain / Surface.
    /// </summary>
    public sealed class MergeFailureClassifier : IMergeFailureClassifier
    {
        /// <inheritdoc />
        public MergeFailureSignal Classify(
            GitMergeOutcome git,
            TestRunOutcome? tests,
            IReadOnlyList<string> conflictedFiles)
        {
            if (git == null) throw new ArgumentNullException(nameof(git));

            IReadOnlyList<string> normalizedConflicts = conflictedFiles ?? Array.Empty<string>();
            int conflictCount = normalizedConflicts.Count;
            bool testsRan = tests != null;
            bool testsFailed = tests != null && tests.ExitCode != 0;

            // Rule 1: stale-base shape -- merge fold attempted, didn't succeed,
            // and the porcelain status reported no conflict markers. The M1
            // GitMergeOutcome shape doesn't carry an explicit non-fast-forward
            // flag; this maps the brief's "NonFastForward && no conflicts"
            // intent onto the shape we have. Tests must not be present (a
            // post-merge test failure with no conflicts is a different shape
            // and should fall through to rule 3).
            if (git.MergeAttempted && !git.MergeSucceeded && conflictCount == 0 && !testsRan)
            {
                return new MergeFailureSignal(
                    MergeFailureClass.StaleBase,
                    "Merge fold failed without conflict markers (stale base / non-fast-forward)",
                    Array.Empty<string>());
            }

            // Rule 2: text conflict -- porcelain status reported one or more
            // conflicted files and no test result is present (tests never ran
            // because the fold blocked).
            if (conflictCount > 0 && !testsRan)
            {
                string filesPreview = BuildFilesPreview(normalizedConflicts);
                string summary = "Merge fold produced " + conflictCount + " conflicted file(s)" + filesPreview;
                return new MergeFailureSignal(
                    MergeFailureClass.TextConflict,
                    Truncate(summary, 512),
                    normalizedConflicts);
            }

            // Rule 3: tests failed AFTER a clean merge fold. Captain branch
            // merged cleanly, but the post-merge test command exited non-zero.
            if (testsFailed && git.MergeSucceeded)
            {
                return new MergeFailureSignal(
                    MergeFailureClass.TestFailureAfterMerge,
                    "Tests failed after clean merge fold (exit " + tests!.ExitCode + ")",
                    Array.Empty<string>());
            }

            // Rule 4: tests failed BEFORE a clean merge fold. Captain branch
            // produced broken work; not a recovery case (auto-recovery routes
            // these to surface so the Judge / NEEDS_REVISION path handles it).
            if (testsFailed && !git.MergeSucceeded)
            {
                return new MergeFailureSignal(
                    MergeFailureClass.TestFailureBeforeMerge,
                    "Tests failed without a clean merge fold (exit " + tests!.ExitCode + ")",
                    normalizedConflicts);
            }

            // Rule 5: unparseable / contradictory -- conservative default so
            // the router surfaces rather than guesses.
            return new MergeFailureSignal(
                MergeFailureClass.Unknown,
                BuildUnknownSummary(git, tests, conflictCount),
                normalizedConflicts);
        }

        private static string BuildFilesPreview(IReadOnlyList<string> files)
        {
            if (files.Count == 0) return "";
            int previewCount = Math.Min(files.Count, 3);
            string[] preview = new string[previewCount];
            for (int i = 0; i < previewCount; i++) preview[i] = files[i];
            string joined = String.Join(", ", preview);
            if (files.Count > previewCount) joined += ", ...";
            return " (" + joined + ")";
        }

        private static string BuildUnknownSummary(GitMergeOutcome git, TestRunOutcome? tests, int conflictCount)
        {
            string mergeState;
            if (!git.MergeAttempted) mergeState = "merge_not_attempted";
            else if (git.MergeSucceeded) mergeState = "merge_succeeded";
            else mergeState = "merge_failed";

            string testsState;
            if (tests == null) testsState = "tests_absent";
            else if (tests.ExitCode == 0) testsState = "tests_passed";
            else testsState = "tests_failed_exit_" + tests.ExitCode;

            return "Unknown failure shape (" + mergeState + ", " + testsState + ", conflicts=" + conflictCount + ")";
        }

        private static string Truncate(string s, int max)
        {
            if (String.IsNullOrEmpty(s)) return s;
            if (s.Length <= max) return s;
            return s.Substring(0, max);
        }
    }
}
