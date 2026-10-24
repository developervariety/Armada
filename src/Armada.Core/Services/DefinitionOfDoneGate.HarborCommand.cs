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
        private async Task<DefinitionOfDoneResult> RunHarborCommandAsync(
            string label, string command, string address, CancellationToken token,
            Regex? failedTestLinePattern, IReadOnlyDictionary<string, string>? overrides, WorkflowProfile? profile)
        {
            HarborCheckoutAddress? parsed;
            if (!HarborCheckoutAddress.TryParse(address, out parsed) || parsed == null || parsed.CheckoutId != null
                || parsed.UseRepository || parsed.UseWorkingDirectory || parsed.RelativePath.Length != 0)
                return DefinitionOfDoneResult.Fail(label, -1, "The Harbor gate address is not a primary dock checkout.", DefinitionOfDoneFailureClassEnum.Infra);
            if (HarborSettings == null || !HarborSettings.Enabled || HarborCheckouts == null)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_owned_gate_unavailable: the checkout service is not enabled.", DefinitionOfDoneFailureClassEnum.Infra);
            Dictionary<string, string> environment = MergeCommandEnvironment(profile, overrides);
            if (environment.Count > 0)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_owned_gate_profile_environment_unsupported", DefinitionOfDoneFailureClassEnum.Infra);
            Dock? dock = await _Database.Docks.ReadAsync(parsed.DockId, token).ConfigureAwait(false);
            GateSlot? slot = _GateSlot.Value;
            if (dock == null || !dock.Active || dock.HostCheckout == null || slot == null || _Git == null)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_owned_gate_binding_missing", DefinitionOfDoneFailureClassEnum.Infra);
            Mission? mission = await _Database.Missions.ReadAsync(slot.MissionId, token).ConfigureAwait(false);
            if (mission == null || mission.DockId != dock.Id || mission.TenantId != dock.TenantId
                || mission.UserId != dock.UserId || mission.VesselId != dock.VesselId)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_owned_gate_mission_binding_mismatch", DefinitionOfDoneFailureClassEnum.Infra);
            string commandKey;
            if (label.StartsWith("build", StringComparison.OrdinalIgnoreCase)) commandKey = HarborVerificationProtocol.DefinitionOfDoneBuild;
            else if (label.StartsWith("unit-test", StringComparison.OrdinalIgnoreCase)) commandKey = HarborVerificationProtocol.DefinitionOfDoneUnitTest;
            else return DefinitionOfDoneResult.Fail(label, -1, "harbor_owned_gate_command_unsupported", DefinitionOfDoneFailureClassEnum.Infra);
            if (label.Length > 120)
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_owned_gate_label_too_long", DefinitionOfDoneFailureClassEnum.Infra);
            string? expectedHead = await _Git!.GetHeadCommitHashAsync(address, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(expectedHead))
                return DefinitionOfDoneResult.Fail(label, -1, "harbor_owned_gate_head_unavailable", DefinitionOfDoneFailureClassEnum.Infra);
            TimeSpan timeout = ResolveCommandTimeout(ResolveCommandTimeoutSeconds(profile));
            string checkId = slot.OperationId + ":" + commandKey + ":" + label;
            HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.RunVerification);
            request.ExpectedHead = expectedHead;
            request.Verification = new HarborVerificationRequest
            {
                CommandKey = commandKey,
                ExpectedCommandSha256 = HarborVerificationProtocol.CommandSha256(command),
                CheckId = checkId,
                TimeoutSeconds = Math.Max(1, Math.Min(WorkflowProfile.MaximumGateCommandTimeoutSeconds, (int)Math.Ceiling(timeout.TotalSeconds))),
                OutputLimitBytes = _CommandOutputLimitBytes,
                AcceptedExitCodes = new List<int> { 0 }
            };
            Stopwatch elapsed = new Stopwatch();
            HarborWorkspaceOperationResult completed;
            HarborVerificationOutputText output;
            IDisposable? standaloneLease = null;
            Stopwatch slotWait = Stopwatch.StartNew();
            if (slot.Lease == null)
                standaloneLease = await HostWideCommandLock.AcquireAsync(slot.MissionId, "definition-of-done " + label, slot.VesselId, token).ConfigureAwait(false);
            else
                await slot.EnsureHeldAsync(token).ConfigureAwait(false);
            slotWait.Stop();
            if (slotWait.Elapsed >= TimeSpan.FromSeconds(1))
                _Logging.Info(_Header + label + " command waited "
                    + slotWait.Elapsed.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " s for the host command slot; its time limit starts now");
            IDisposable? heldLease = slot.Lease ?? standaloneLease;
            long shareMark = HostWideCommandLock.ReadShareMark(heldLease);
            bool sharedHost = false;
            elapsed.Start();
            try
            {
                using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope(checkId, expectedHead))
                {
                    completed = await HarborCheckouts.ExecuteAsync(dock, request, token).ConfigureAwait(false);
                    output = await HarborVerificationOutputReader.ReadAsync(HarborCheckouts, dock, completed, checkId, expectedHead, token).ConfigureAwait(false);
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
                    BuildDiagnosticText("Harbor verification did not complete: " + exception.Message), DefinitionOfDoneFailureClassEnum.Infra);
            }
            finally
            {
                sharedHost = HostWideCommandLock.SharedSince(heldLease, shareMark);
                standaloneLease?.Dispose();
            }
            elapsed.Stop();
            TimeSpan commandDuration = TimeSpan.FromMilliseconds(completed.DurationMilliseconds ?? elapsed.ElapsedMilliseconds);
            return BuildHarborCommandResult(label, command, commandKey, checkId, expectedHead, dock, timeout,
                failedTestLinePattern, completed, output, sharedHost, commandDuration);
        }
    }
}
