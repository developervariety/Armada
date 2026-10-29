namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Grades one battery task attempt per its manifest: commands, byte comparison and mutation run in the task's
    /// worktree on the host that ran it.
    /// </summary>
    public static class BatteryTaskGrader
    {
        #region Public-Members

        /// <summary>
        /// Default timeout of a grader command.
        /// </summary>
        public const int DefaultCommandTimeoutSeconds = 600;

        /// <summary>
        /// Longest output excerpt kept per command.
        /// </summary>
        public const int MaxExcerptChars = 2000;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Probe a workspace for the toolchain a task needs.
        /// </summary>
        /// <param name="pack">Pack.</param>
        /// <param name="task">Task.</param>
        /// <param name="workspace">Workspace.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What is missing (empty when the toolchain is complete).</returns>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public static async Task<BatteryToolchainCheck> CheckToolchainAsync(BatteryPack pack, BatteryTaskDefinition task, IBatteryWorkspace workspace, CancellationToken token = default)
        {
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));

            BatteryToolchainCheck check = new BatteryToolchainCheck();
            List<string> reasons = new List<string>();
            foreach (string name in pack.ToolchainFor(task))
            {
                BatteryToolchainRequirement? tool = pack.Manifest?.Toolchain.FirstOrDefault(t => t != null && String.Equals(t.Name, name, StringComparison.Ordinal));
                if (tool == null || String.IsNullOrWhiteSpace(tool.Executable)) continue;
                BoundedProcessResult result = await workspace.RunAsync(tool.Executable!, new List<string>(tool.Arguments), 60000, token).ConfigureAwait(false);
                if (result.Succeeded) continue;
                check.Missing.Add(name);
                string detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? ("exit code " + (result.ExitCode ?? -1));
                reasons.Add(name + (String.IsNullOrWhiteSpace(tool.Hint) ? String.Empty : " (" + tool.Hint + ")") + ": " + detail);
            }

            if (check.Missing.Count > 0) check.Reason = Capitalize(workspace.HostLabel) + " lacks the pack's toolchain: " + String.Join("; ", reasons) + ".";
            return check;
        }

        /// <summary>
        /// Grade a task attempt.
        /// </summary>
        /// <param name="pack">Pack.</param>
        /// <param name="task">Task.</param>
        /// <param name="workspace">The task's worktree, or null when there is none.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The grade.</returns>
        /// <exception cref="ArgumentNullException">Thrown when pack or task is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the task has no grader.</exception>
        public static async Task<BatteryGrade> GradeAsync(BatteryPack pack, BatteryTaskDefinition task, IBatteryWorkspace? workspace, CancellationToken token = default)
        {
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            if (task == null) throw new ArgumentNullException(nameof(task));
            BatteryGraderDefinition grader = task.Grader ?? throw new ArgumentException("Task " + task.Id + " has no grader.", nameof(task));
            BatteryGraderTypeEnum type = grader.Type ?? throw new ArgumentException("Task " + task.Id + " has no grader type.", nameof(task));

            if (workspace == null) return CouldNotRun("There is no worktree to grade (the mission produced no dock).");

            switch (type)
            {
                case BatteryGraderTypeEnum.Command:
                    return await GradeCommandsAsync(pack, grader, workspace, token).ConfigureAwait(false);
                case BatteryGraderTypeEnum.ByteComparison:
                    return await GradeBytesAsync(pack, grader, workspace, token).ConfigureAwait(false);
                default:
                    return await GradeMutationAsync(pack, grader, workspace, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Copy an overlay directory from the pack into the workspace (files are written as UTF-8 text).
        /// </summary>
        /// <param name="pack">Pack.</param>
        /// <param name="overlay">Overlay directory relative to the pack, or null for none.</param>
        /// <param name="workspace">Workspace.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Files written.</returns>
        /// <exception cref="ArgumentNullException">Thrown when pack or workspace is null.</exception>
        public static async Task<int> ApplyOverlayAsync(BatteryPack pack, string? overlay, IBatteryWorkspace workspace, CancellationToken token = default)
        {
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            if (String.IsNullOrWhiteSpace(overlay)) return 0;
            string root = pack.Resolve(overlay);
            int written = 0;
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                string content = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
                await workspace.WriteTextAsync(relative, content, token).ConfigureAwait(false);
                written++;
            }

            return written;
        }

        #endregion

        #region Private-Methods

        private static async Task<BatteryGrade> GradeCommandsAsync(BatteryPack pack, BatteryGraderDefinition grader, IBatteryWorkspace workspace, CancellationToken token)
        {
            BatteryGrade grade = new BatteryGrade();
            await ApplyOverlayAsync(pack, grader.Overlay, workspace, token).ConfigureAwait(false);
            bool passed = await RunCommandsAsync(grader, workspace, grade.Details.Commands, token).ConfigureAwait(false);
            int ok = grade.Details.Commands.Count(c => c.Passed);
            grade.Score = grader.Commands.Count == 0 ? 0 : (double)ok / grader.Commands.Count;
            grade.Grade = passed ? BatteryTaskGradeEnum.Passed : BatteryTaskGradeEnum.Failed;
            BatteryCommandOutcome? failed = grade.Details.Commands.FirstOrDefault(c => !c.Passed);
            grade.Reason = failed == null ? null : (failed.TimedOut ? "Timed out: " : "Failed (exit code " + failed.ExitCode + "): ") + failed.CommandLine;
            grade.Summary = ok + " of " + grader.Commands.Count + " commands passed.";
            return grade;
        }

        private static async Task<BatteryGrade> GradeBytesAsync(BatteryPack pack, BatteryGraderDefinition grader, IBatteryWorkspace workspace, CancellationToken token)
        {
            BatteryGrade grade = new BatteryGrade();
            foreach (BatteryExpectedFile file in grader.Files.Where(f => f != null))
            {
                byte[] expected = await File.ReadAllBytesAsync(pack.Resolve(file.Expected), token).ConfigureAwait(false);
                BoundedProcessResult hash = await workspace.RunAsync("git", new List<string> { "hash-object", "--no-filters", "--", file.Path! }, 60000, token).ConfigureAwait(false);
                string? actual = hash.Succeeded ? FirstLine(hash.StandardOutput) : null;
                grade.Details.Files.Add(BatteryByteComparison.Compare(file.Path!, expected, actual));
            }

            int matched = grade.Details.Files.Count(f => f.Matched);
            grade.Score = grade.Details.Files.Count == 0 ? 0 : (double)matched / grade.Details.Files.Count;
            grade.Grade = matched == grade.Details.Files.Count && matched > 0 ? BatteryTaskGradeEnum.Passed : BatteryTaskGradeEnum.Failed;
            grade.Reason = grade.Grade == BatteryTaskGradeEnum.Passed
                ? null
                : String.Join(" ", grade.Details.Files.Where(f => !f.Matched).Select(f => f.Path + ": " + f.Reason));
            grade.Summary = matched + " of " + grade.Details.Files.Count + " files byte-identical to the expected result.";
            return grade;
        }

        private static async Task<BatteryGrade> GradeMutationAsync(BatteryPack pack, BatteryGraderDefinition grader, IBatteryWorkspace workspace, CancellationToken token)
        {
            BatteryGrade grade = new BatteryGrade();
            await ApplyOverlayAsync(pack, grader.Overlay, workspace, token).ConfigureAwait(false);
            bool baseline = await RunCommandsAsync(grader, workspace, grade.Details.Commands, token).ConfigureAwait(false);
            if (!baseline)
            {
                BatteryCommandOutcome failed = grade.Details.Commands.First(c => !c.Passed);
                grade.Grade = BatteryTaskGradeEnum.Failed;
                grade.Score = 0;
                grade.Reason = "The tests do not pass before mutation: " + failed.CommandLine + " (exit code " + failed.ExitCode + ").";
                grade.Summary = "Tests fail without mutants.";
                return grade;
            }

            foreach (BatteryMutant mutant in grader.Mutants.Where(m => m != null))
            {
                BatteryMutantOutcome outcome = new BatteryMutantOutcome { Id = mutant.Id ?? String.Empty };
                string? original = await workspace.ReadTextAsync(mutant.File!, token).ConfigureAwait(false);
                if (original == null || mutant.Find == null || original.IndexOf(mutant.Find, StringComparison.Ordinal) < 0)
                {
                    outcome.Reason = original == null ? "The file is missing." : "The mutated code is not in the file (was the source changed?).";
                    grade.Details.Mutants.Add(outcome);
                    continue;
                }

                outcome.Applied = true;
                try
                {
                    await workspace.WriteTextAsync(mutant.File!, ReplaceFirst(original, mutant.Find, mutant.Replace ?? String.Empty), token).ConfigureAwait(false);
                    List<BatteryCommandOutcome> runs = new List<BatteryCommandOutcome>();
                    bool survived = await RunCommandsAsync(grader, workspace, runs, token).ConfigureAwait(false);
                    outcome.Killed = !survived;
                    BatteryCommandOutcome? killer = runs.FirstOrDefault(c => !c.Passed);
                    outcome.Reason = survived ? "The tests still pass with the mutant." : "Killed by " + killer?.CommandLine + " (exit code " + killer?.ExitCode + ").";
                }
                finally
                {
                    await workspace.WriteTextAsync(mutant.File!, original, CancellationToken.None).ConfigureAwait(false);
                }

                grade.Details.Mutants.Add(outcome);
            }

            int total = grade.Details.Mutants.Count;
            int killed = grade.Details.Mutants.Count(m => m.Killed);
            grade.Score = total == 0 ? 0 : (double)killed / total;
            grade.Grade = total > 0 && killed == total ? BatteryTaskGradeEnum.Passed : BatteryTaskGradeEnum.Failed;
            grade.Reason = grade.Grade == BatteryTaskGradeEnum.Passed
                ? null
                : "Mutants not killed: " + String.Join(", ", grade.Details.Mutants.Where(m => !m.Killed).Select(m => m.Id + " (" + m.Reason + ")")) + ".";
            grade.Summary = "Tests pass; " + killed + " of " + total + " mutants killed.";
            return grade;
        }

        private static async Task<bool> RunCommandsAsync(BatteryGraderDefinition grader, IBatteryWorkspace workspace, List<BatteryCommandOutcome> outcomes, CancellationToken token)
        {
            foreach (BatteryCommand command in grader.Commands.Where(c => c != null))
            {
                int timeoutMs = (command.TimeoutSeconds ?? DefaultCommandTimeoutSeconds) * 1000;
                BoundedProcessResult result = await workspace.RunAsync(command.Executable!, new List<string>(command.Arguments), timeoutMs, token).ConfigureAwait(false);
                BatteryCommandOutcome outcome = new BatteryCommandOutcome
                {
                    CommandLine = (command.Executable + " " + String.Join(" ", command.Arguments)).Trim(),
                    ExitCode = result.ExitCode ?? -1,
                    TimedOut = result.TimedOut,
                    Passed = !result.TimedOut && result.ExitCode == command.ExpectedExitCode,
                    OutputExcerpt = Tail(result.StandardOutput + result.StandardError)
                };
                outcomes.Add(outcome);
                if (!outcome.Passed) return false;
            }

            return true;
        }

        private static BatteryGrade CouldNotRun(string reason)
        {
            return new BatteryGrade { Grade = BatteryTaskGradeEnum.CouldNotRun, Reason = reason, Summary = "Could not run." };
        }

        private static string ReplaceFirst(string text, string find, string replace)
        {
            int index = text.IndexOf(find, StringComparison.Ordinal);
            return text.Substring(0, index) + replace + text.Substring(index + find.Length);
        }

        private static string? FirstLine(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            return text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }

        private static string? Tail(string text)
        {
            if (String.IsNullOrEmpty(text)) return null;
            return text.Length <= MaxExcerptChars ? text : text.Substring(text.Length - MaxExcerptChars);
        }

        private static string Capitalize(string text)
        {
            return text.Length == 0 ? text : Char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        #endregion
    }
}
