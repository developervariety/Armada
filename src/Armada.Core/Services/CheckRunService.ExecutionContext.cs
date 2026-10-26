namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class CheckRunService
    {
        /// <summary>Evidence of the context captured for a full native Check execution.</summary>
        public const string ExecutionContextEventType = "check.execution_context";

        internal async Task<bool> HasMatchingExecutionContextAsync(CheckRun run, Vessel vessel, WorkflowProfile profile, CancellationToken token)
        {
            if (!String.Equals(run.TenantId ?? Constants.DefaultTenantId, vessel.TenantId ?? Constants.DefaultTenantId, StringComparison.Ordinal)
                || !String.Equals(run.UserId ?? Constants.DefaultUserId, vessel.UserId ?? Constants.DefaultUserId, StringComparison.Ordinal)) return false;
            if (run.Source != CheckRunSourceEnum.Armada || run.ExitCode != 0 || run.SatisfiedBy != null
                || !String.IsNullOrWhiteSpace(run.DeploymentId) || !String.IsNullOrWhiteSpace(run.EnvironmentName)
                || !String.Equals(run.WorkflowProfileId, profile.Id, StringComparison.Ordinal)) return false;

            // Integration uses a missionless Check. A native receipt cannot authorize a Harbor route;
            // remote results run again until their current authenticated binding can be proved.
            CheckRun requested = new CheckRun
            {
                VesselId = vessel.Id, WorkflowProfileId = profile.Id, Type = run.Type,
                TenantId = vessel.TenantId, UserId = vessel.UserId
            };
            if (await IsHarborCheckSelectedAsync(requested, vessel, token).ConfigureAwait(false)) return false;

            List<ArmadaEvent> events = await _Database.Events.EnumerateByEntityAsync("check_run", run.Id, 50, token).ConfigureAwait(false);
            ArmadaEvent? recorded = events.Where(evt => evt.EventType == ExecutionContextEventType)
                .OrderByDescending(evt => evt.CreatedUtc).FirstOrDefault();
            if (recorded == null || String.IsNullOrWhiteSpace(recorded.Payload)) return false;
            CheckExecutionContextReceipt? receipt = JsonSerializer.Deserialize<CheckExecutionContextReceipt>(recorded.Payload);
            if (receipt == null || receipt.Version != 1 || !String.Equals(receipt.CheckRunId, run.Id, StringComparison.Ordinal)) return false;
            Dictionary<string, string> environment = await BuildCommandEnvironmentAsync(profile, run, token).ConfigureAwait(false);
            string command = IsIsolatedCheckoutType(run.Type) && ResolveRepoSource(vessel) != null ? StripNoRestore(run.Command) : run.Command;
            CheckExecutionContextReceipt expected = CreateExecutionContextReceipt(run, vessel, profile, command, environment);
            return String.Equals(receipt.Fingerprint, expected.Fingerprint, StringComparison.Ordinal);
        }

        private CheckExecutionContextReceipt? CaptureExecutionContext(CheckRun run, Vessel vessel, WorkflowProfile? profile,
            string executionCommand, IReadOnlyDictionary<string, string> environment)
        {
            try { return CreateExecutionContextReceipt(run, vessel, profile, executionCommand, environment); }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "check " + run.Id + " context could not be captured; it will not be reusable: " + ex.GetType().Name);
                return null;
            }
        }

        private CheckExecutionContextReceipt CreateExecutionContextReceipt(CheckRun run, Vessel vessel, WorkflowProfile? profile,
            string executionCommand, IReadOnlyDictionary<string, string> environment)
        {
            CheckCommandBudget budget = CreateCommandBudget(profile, run.Type);
            string context = JsonSerializer.Serialize(new
            {
                Version = 1, Route = "Native", CheckRunId = run.Id, run.TenantId, run.UserId, run.VesselId,
                run.Type, Command = run.Command.Trim(), run.CommitHash, run.WorkflowProfileId,
                Profile = profile, ExecutionCommand = executionCommand,
                Environment = DefinitionOfDoneCommandPasses.FingerprintEnvironment(environment),
                BudgetTicks = budget.Limit.Ticks, SharedBudget = budget.Shared,
                RepositorySource = ResolveRepoSource(vessel), vessel.WorkingDirectory,
                Platform = RuntimeInformation.RuntimeIdentifier, Runtime = RuntimeInformation.FrameworkDescription
            });
            return new CheckExecutionContextReceipt
            {
                Version = 1, CheckRunId = run.Id,
                Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context))).ToLowerInvariant()
            };
        }

        private async Task RecordExecutionContextAsync(CheckRun run, CheckExecutionContextReceipt? receipt, CancellationToken token)
        {
            if (receipt == null || run.Status != CheckRunStatusEnum.Passed || run.SatisfiedBy != null
                || !String.Equals(receipt.CheckRunId, run.Id, StringComparison.Ordinal)) return;
            try
            {
                ArmadaEvent evidence = new ArmadaEvent(ExecutionContextEventType, "Full native Check execution context captured")
                {
                    EntityType = "check_run", EntityId = run.Id, VesselId = run.VesselId, MissionId = run.MissionId,
                    TenantId = run.TenantId ?? Constants.DefaultTenantId, UserId = run.UserId ?? Constants.DefaultUserId,
                    Payload = JsonSerializer.Serialize(receipt)
                };
                await _Database.Events.CreateAsync(evidence, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "check " + run.Id + " has no reusable execution receipt: " + ex.GetType().Name);
            }
        }
    }
}
