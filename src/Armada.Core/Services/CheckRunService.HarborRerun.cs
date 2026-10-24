namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class CheckRunService
    {
        private async Task ApplyHarborIsolatedRerunAsync(CheckRun run, WorkflowProfile? profile,
            CommandExecutionResult execution, string baseCommand, HarborCheckExecution first, CancellationToken token)
        {
            if (run.Status != CheckRunStatusEnum.Failed || !IsTestCheckType(run.Type) || !IsolatedRerunEnabled()) return;
            DefinitionOfDoneFailureClassEnum failureClass = new DefinitionOfDoneFailureClassifier().Classify("unit-test", execution.ExitCode, execution.Output);
            Regex? pattern = null;
            if (!String.IsNullOrWhiteSpace(profile?.FailedTestLinePattern)
                && !FailedTestLinePattern.TryCreate(profile!.FailedTestLinePattern, FailedTestLinePattern.MatchTimeout, out pattern, out string? patternError))
            {
                _Logging.Warn(_Header + "check " + run.Id + " failed-test pattern ignored: " + patternError);
                pattern = null;
            }
            FailedTestNameExtractor.FailedTestNameSet failing = failureClass == DefinitionOfDoneFailureClassEnum.TestFail
                ? FailedTestNameExtractor.Extract(execution.Output, pattern)
                : FailedTestNameExtractor.FailedTestNameSet.Empty;
            IsolatedRerunPlan plan = IsolatedRerunPlan.ForFailedRun(profile, baseCommand, failureClass,
                failing.Names, failing.Overflow || failing.PatternTimedOut || execution.Truncated);
            if (plan.NotTestFailure) return;
            if (plan.Command == null)
            {
                string noRerun = plan.NotRerunReason ?? IsolatedRerunPlan.NoCommandReason;
                run.Summary += " Not re-run in isolation: " + noRerun + ".";
                run.Output += Environment.NewLine + Environment.NewLine + "Not re-run in isolation: " + noRerun + ".";
                return;
            }
            if (first.Result.VerificationOutputId == null)
                throw new InvalidOperationException("harbor_verification_output_id_missing");
            string rerunCheckId = run.Id + ":rerun";
            if (rerunCheckId.Length > 200) rerunCheckId = run.Id.Substring(0, 194) + ":rerun";
            HarborVerificationRequest verification = new HarborVerificationRequest
            {
                CommandKey = HarborVerificationProtocol.CheckCommandKey(run.Type),
                ExpectedCommandSha256 = HarborVerificationProtocol.CommandSha256(baseCommand),
                CheckId = rerunCheckId,
                TimeoutSeconds = Math.Max(1, Math.Min(1800, (int)Math.Ceiling(_CommandTimeout.TotalSeconds))),
                OutputLimitBytes = CheckCommandOutputLimitBytes,
                AcceptedExitCodes = new List<int> { 0 },
                Rerun = new HarborVerificationRerunRequest
                {
                    PriorOutputId = first.Result.VerificationOutputId,
                    PriorCheckId = run.Id,
                    FailingTestNames = new List<string>(failing.Names),
                    FailedTestLinePattern = pattern == null ? null : profile?.FailedTestLinePattern,
                    RerunCommandKey = plan.FromProfile ? HarborVerificationProtocol.CheckRerunCommandKey(run.Type) : null,
                    RerunCommandSha256 = plan.FromProfile ? HarborVerificationProtocol.CommandSha256(plan.Command) : null
                }
            };
            HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(first.Dock, HarborWorkspaceOperationEnum.RunVerification);
            request.Verification = verification;
            HarborWorkspaceOperationResult rerunResult;
            HarborVerificationOutputText output;
            try
            {
                using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("check:" + run.Id + ":owned-rerun", first.Binding.BaseCommit))
                {
                    rerunResult = await HarborCheckouts!.ExecuteAsync(first.Dock, request, token).ConfigureAwait(false);
                    output = await HarborVerificationOutputReader.ReadAsync(HarborCheckouts, first.Dock, rerunResult,
                        rerunCheckId, first.Binding.BaseCommit, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                string reason = "The isolated re-run did not complete: " + exception.Message + ".";
                run.Summary += " " + reason;
                run.Output += Environment.NewLine + Environment.NewLine + reason;
                return;
            }
            bool passed = rerunResult.Accepted && rerunResult.ExitCode.HasValue && rerunResult.ExitCode.Value == 0
                && !rerunResult.TimedOut && !rerunResult.Cancelled && !rerunResult.Truncated;
            string fullRunExit = run.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            string names = String.Join(", ", failing.Names);
            string rerunText = "Isolated re-run of " + plan.Count + " failing " + plan.Unit + " (" + names + ") "
                + (passed ? "passed." : "also failed (exit " + (rerunResult.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown") + ").");
            string rerunOutput = CombineOutput(output.StandardOutput, output.StandardError);
            run.Output += Environment.NewLine + Environment.NewLine + "--- ISOLATED RE-RUN ---" + Environment.NewLine
                + rerunText + Environment.NewLine + BoundRerunOutput(rerunOutput);
            if (passed)
            {
                string label = !String.IsNullOrWhiteSpace(run.Label) ? run.Label! : run.Type.ToString();
                run.Status = CheckRunStatusEnum.Passed;
                run.ExitCode = 0;
                run.Summary = label + " passed on isolated re-run. The full run failed (exit " + fullRunExit + "). " + rerunText;
            }
            else run.Summary += " " + rerunText;
        }
    }
}
