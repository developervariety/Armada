namespace Armada.Core.Services
{
    using System;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class DefinitionOfDoneGate
    {
        private DefinitionOfDoneResult BuildHarborCommandResult(
            string label, string command, string commandKey, string checkId, string expectedHead, Dock dock,
            TimeSpan timeout, Regex? failedTestLinePattern, HarborWorkspaceOperationResult completed,
            HarborVerificationOutputText output, bool sharedHost, TimeSpan commandDuration)
        {
            bool timedOut = completed.TimedOut || completed.Cancelled;
            bool incomplete = timedOut || completed.Truncated;
            int exitCode = timedOut ? -1 : completed.ExitCode ?? -1;
            string combined = CombineOutput(output.StandardOutput, output.StandardError);
            string metricOutcome = completed.Cancelled ? ArmadaMetrics.OutcomeCanceled
                : timedOut ? ArmadaMetrics.OutcomeTimeout
                : exitCode == 0 ? ArmadaMetrics.OutcomePassed : ArmadaMetrics.OutcomeFailed;
            ArmadaMetrics.RecordValidation(ArmadaMetrics.ValidationDefinitionOfDone, commandDuration, metricOutcome);
            DefinitionOfDoneResult result;
            if (timedOut)
            {
                string message = label + " command timed out after " + timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " seconds.";
                string timeoutOutput = String.IsNullOrWhiteSpace(combined) ? message : message + "\n" + combined;
                FailedTestNameExtractor.FailedTestNameSet failures = FailedTestNameExtractor.Extract(combined, failedTestLinePattern);
                result = DefinitionOfDoneResult.Fail(label, -1, BuildDiagnosticText(timeoutOutput, failedTestLinePattern),
                    failures.Names.Count > 0 ? DefinitionOfDoneFailureClassEnum.TestFail : DefinitionOfDoneFailureClassEnum.Timeout);
                result.TimedOut = true;
                result.TimedOutOnSharedHost = sharedHost;
                result.TimedOutWhileProgressing = false;
                if (failures.Names.Count > 0)
                {
                    result.FailedTestNames = failures.Names;
                    result.FailedTestNamesOverflow = true;
                }
                combined = timeoutOutput;
            }
            else if (exitCode == 0)
            {
                result = DefinitionOfDoneResult.Pass();
            }
            else
            {
                DefinitionOfDoneFailureClassEnum classification = _FailureClassifier.Classify(label, exitCode, combined);
                result = DefinitionOfDoneResult.Fail(label, exitCode, BuildDiagnosticText(combined, failedTestLinePattern), classification);
                if (classification == DefinitionOfDoneFailureClassEnum.TestFail)
                {
                    FailedTestNameExtractor.FailedTestNameSet failures = FailedTestNameExtractor.Extract(combined, failedTestLinePattern);
                    result.FailedTestNames = failures.Names;
                    result.FailedTestNamesOverflow = failures.Overflow || incomplete;
                }
            }
            result.GateCommandOutput = combined;
            result.RunnerOutputTruncated = completed.Truncated;
            result.RunnerOutputOmittedBytes = completed.StandardOutputOmittedBytes + completed.StandardErrorOmittedBytes;
            result.RunnerOutputIncomplete = incomplete || !completed.KillConfirmed && timedOut;
            result.TimedOutOnSharedHost = timedOut && sharedHost;
            result.HarborOutputId = completed.VerificationOutputId;
            result.HarborCheckId = checkId;
            result.HarborDockId = dock.Id;
            result.HarborExpectedHead = expectedHead;
            result.HarborCommandKey = commandKey;
            result.HarborCommandSha256 = HarborVerificationProtocol.CommandSha256(command);
            return result;
        }
    }
}
