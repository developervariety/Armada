namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.Text.RegularExpressions;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class DefinitionOfDoneGate
    {
        private async Task<DefinitionOfDoneResult> RunHarborRerunAsync(
            string label, WorkflowProfile? profile, IsolatedRerunPlan plan,
            DefinitionOfDoneResult source, string address, Regex? failurePattern, CancellationToken token)
        {
            if (plan.Command == null || source.HarborOutputId == null || source.HarborCheckId == null
                || source.HarborDockId == null || source.HarborExpectedHead == null
                || source.HarborCommandKey == null || source.HarborCommandSha256 == null
                || source.FailedTestNames == null || source.FailedTestNames.Count == 0 || source.FailedTestNamesOverflow)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_verification_rerun_source_incomplete", DefinitionOfDoneFailureClassEnum.Infra);
            if (!HarborCheckoutAddress.TryParse(address, out HarborCheckoutAddress? parsed) || parsed == null
                || parsed.DockId != source.HarborDockId || parsed.CheckoutId != null)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_verification_rerun_binding_mismatch", DefinitionOfDoneFailureClassEnum.Infra);
            Dock? dock = await _Database.Docks.ReadAsync(parsed.DockId, token).ConfigureAwait(false);
            if (dock == null || !dock.Active || dock.HostCheckout == null)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_verification_rerun_dock_missing", DefinitionOfDoneFailureClassEnum.Infra);
            GateSlot? slot = _GateSlot.Value;
            if (slot == null)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_verification_rerun_scope_missing", DefinitionOfDoneFailureClassEnum.Infra);
            Mission? mission = await _Database.Missions.ReadAsync(slot.MissionId, token).ConfigureAwait(false);
            if (mission == null || mission.DockId != dock.Id || mission.TenantId != dock.TenantId
                || mission.UserId != dock.UserId || mission.VesselId != dock.VesselId)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_verification_rerun_mission_mismatch", DefinitionOfDoneFailureClassEnum.Infra);
            string checkId = slot.OperationId + ":rerun:" + label;
            if (checkId.Length > 200) return DefinitionOfDoneResult.Fail(label, -1, "harbor_verification_rerun_id_invalid", DefinitionOfDoneFailureClassEnum.Infra);
            string? rerunCommandKey = plan.FromProfile ? HarborVerificationProtocol.DefinitionOfDoneUnitTestRerun : null;
            string? rerunCommandHash = plan.FromProfile
                ? HarborVerificationProtocol.CommandSha256(profile?.IsolatedTestRerunCommand ?? String.Empty)
                : null;
            HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.RunVerification);
            request.ExpectedHead = source.HarborExpectedHead;
            request.Verification = new HarborVerificationRequest
            {
                CommandKey = source.HarborCommandKey,
                ExpectedCommandSha256 = source.HarborCommandSha256,
                CheckId = checkId,
                TimeoutSeconds = Math.Max(1, Math.Min(WorkflowProfile.MaximumGateCommandTimeoutSeconds, ResolveCommandTimeoutSeconds(profile))),
                OutputLimitBytes = _CommandOutputLimitBytes,
                AcceptedExitCodes = new List<int> { 0 },
                Rerun = new HarborVerificationRerunRequest
                {
                    PriorOutputId = source.HarborOutputId,
                    PriorCheckId = source.HarborCheckId,
                    FailingTestNames = new List<string>(source.FailedTestNames),
                    FailedTestLinePattern = failurePattern == null ? null : profile?.FailedTestLinePattern,
                    RerunCommandKey = rerunCommandKey,
                    RerunCommandSha256 = rerunCommandHash
                }
            };
            Stopwatch elapsed = Stopwatch.StartNew();
            HarborWorkspaceOperationResult completed;
            HarborVerificationOutputText output;
            try
            {
                using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope(checkId, source.HarborExpectedHead))
                {
                    completed = await HarborCheckouts!.ExecuteAsync(dock, request, token).ConfigureAwait(false);
                    output = await HarborVerificationOutputReader.ReadAsync(HarborCheckouts, dock, completed, checkId,
                        source.HarborExpectedHead, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                ArmadaMetrics.RecordValidation(ArmadaMetrics.ValidationDefinitionOfDone, elapsed.Elapsed, ArmadaMetrics.OutcomeCanceled);
                throw;
            }
            catch (Exception exception)
            {
                ArmadaMetrics.RecordValidation(ArmadaMetrics.ValidationDefinitionOfDone, elapsed.Elapsed, ArmadaMetrics.OutcomeError);
                return DefinitionOfDoneResult.Fail(label, -1,
                    BuildDiagnosticText("Harbor isolated rerun did not complete: " + exception.Message), DefinitionOfDoneFailureClassEnum.Infra);
            }
            elapsed.Stop();
            string combined = CombineOutput(output.StandardOutput, output.StandardError);
            int exitCode = completed.TimedOut || completed.Cancelled ? -1 : completed.ExitCode ?? -1;
            bool passed = exitCode == 0 && !completed.TimedOut && !completed.Cancelled && !completed.Truncated;
            string outcome = completed.Cancelled ? ArmadaMetrics.OutcomeCanceled
                : completed.TimedOut ? ArmadaMetrics.OutcomeTimeout
                : passed ? ArmadaMetrics.OutcomePassed : ArmadaMetrics.OutcomeFailed;
            ArmadaMetrics.RecordValidation(ArmadaMetrics.ValidationDefinitionOfDone, elapsed.Elapsed, outcome);
            DefinitionOfDoneResult result = passed
                ? DefinitionOfDoneResult.Pass()
                : DefinitionOfDoneResult.Fail(label, exitCode, BuildDiagnosticText(combined, failurePattern),
                    completed.TimedOut ? DefinitionOfDoneFailureClassEnum.Timeout : _FailureClassifier.Classify(label, exitCode, combined));
            result.GateCommandOutput = combined;
            result.RunnerOutputTruncated = completed.Truncated;
            result.RunnerOutputOmittedBytes = completed.StandardOutputOmittedBytes + completed.StandardErrorOmittedBytes;
            result.RunnerOutputIncomplete = completed.Truncated || completed.TimedOut || completed.Cancelled || !completed.KillConfirmed && completed.TimedOut;
            if (!passed && result.FailureClass == DefinitionOfDoneFailureClassEnum.TestFail)
            {
                FailedTestNameExtractor.FailedTestNameSet names = FailedTestNameExtractor.Extract(combined, failurePattern);
                result.FailedTestNames = names.Names;
                result.FailedTestNamesOverflow = names.Overflow || result.RunnerOutputIncomplete;
            }
            return result;
        }
    }
}
