namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Authorization;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>Operator authorization, launch exclusion and capacity boundary for full Research replay.</summary>
    public sealed partial class ResearchReplayService
    {
        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;
        private readonly ArmadaSettings _Settings;
        private readonly IGitService _Git;
        private readonly FleetActionService _Operators;
        private readonly OperationNotifier _Notifier;
        private readonly SyslogLogging.LoggingModule _Logging = new SyslogLogging.LoggingModule();

        /// <summary>Create a Research replay service.</summary>
        /// <param name="database">Durable database.</param>
        /// <param name="admiral">Current dispatch and completion owner.</param>
        /// <param name="settings">Current capacity and checkout settings.</param>
        /// <param name="git">Native Git object resolver.</param>
        /// <param name="notifier">Shared post-commit event and change notifications.</param>
        public ResearchReplayService(DatabaseDriver database, IAdmiralService admiral, ArmadaSettings settings, IGitService git, OperationNotifier? notifier = null)
        {
            _Database = database; _Admiral = admiral; _Settings = settings; _Git = git;
            _Operators = new FleetActionService(database, admiral);
            _Notifier = notifier ?? OperationNotifier.None;
        }

        /// <summary>Preview or apply one explicit full Research chain continuation.</summary>
        /// <param name="caller">Authenticated current global operator.</param>
        /// <param name="voyageId">Original voyage.</param>
        /// <param name="request">Read-only preview or idempotent expected-state request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Safe plan metadata or the committed replay receipt.</returns>
        public async Task<ResearchReplayPlan> ExecuteAsync(AuthContext caller, string voyageId, ResearchReplayRequest request,
            CancellationToken token = default)
        {
            AuthContext actor = await _Operators.RequireOperatorAsync(caller, token).ConfigureAwait(false);
            if (!request.DryRun)
            {
                ResearchReplayPlan? prior = await _Database.ResearchReplays.ReadReceiptAsync(actor.TenantId!, actor.UserId!, voyageId, request, token).ConfigureAwait(false);
                if (prior != null) return prior;
            }
            List<Mission> stages = await _Database.Missions.EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
            List<IDisposable> boundaries = new List<IDisposable>();
            try
            {
                foreach (string id in stages.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal))
                    boundaries.Add(await MissionExecutionBoundary.Locks.AcquireAsync(id, token).ConfigureAwait(false));
                // A completion can synchronously launch a retry of the same mission, so launch writes use a separate boundary.
                foreach (string id in stages.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal))
                    boundaries.Add(await MissionExecutionBoundary.LaunchWrites.AcquireAsync(id, token).ConfigureAwait(false));
                if (stages.Any(m => _Admiral.IsMissionCompletionInFlight(m.Id)))
                    throw new InvalidOperationException("A prior Research completion is still being handled.");
                Voyage currentVoyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("Research voyage not found.");
                ResearchReplayRule.Validate(currentVoyage, stages, actor.TenantId!, actor.UserId!, request.SourceCommit);
                if (stages.Count != 6 || String.IsNullOrWhiteSpace(stages[0].VesselId))
                    throw new InvalidOperationException("The exact six-stage Research inventory is required.");
                Vessel vessel = await _Database.Vessels.ReadAsync(stages[0].VesselId!, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Research source vessel is missing.");
                if (!OwnershipPolicy.SameTenant(vessel.TenantId, actor.TenantId)
                    || OwnershipPolicy.UserOfRecord(vessel.UserId) != actor.UserId)
                    throw new UnauthorizedAccessException("The Research source vessel belongs to another owner.");
                request.CapturedConfigurationDigest = await CaptureConfigurationDigestAsync(actor, vessel, token).ConfigureAwait(false);
                request.CapturedInstructions = await CaptureInstructionsAsync(stages, token).ConfigureAwait(false);
                ResearchReplayPlan preview = await _Database.ResearchReplays.ExecuteAsync(actor.TenantId!, actor.UserId!, voyageId,
                    new ResearchReplayRequest { DryRun = true, SourceCommit = request.SourceCommit,
                        CapturedInstructions = request.CapturedInstructions,
                        CapturedConfigurationDigest = request.CapturedConfigurationDigest }, token).ConfigureAwait(false);
                if (request.DryRun) return preview;
                DispatchHoldSnapshot? hold = _Admiral.DispatchHold?.Snapshot();
                if (hold != null) throw new InvalidOperationException(DispatchHold.RefusalMessage(hold));
                if (String.IsNullOrEmpty(vessel.LocalPath)
                    || !String.Equals(await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, request.SourceCommit, token).ConfigureAwait(false),
                        request.SourceCommit, StringComparison.Ordinal))
                    throw new InvalidOperationException("The exact Research source object is not available; no default-branch fallback is allowed.");
                if (_Settings.Harbor?.MissionRoutes?.Any(route => route != null
                    && route.CheckoutMode == Armada.Core.Enums.HarborCheckoutModeEnum.HarborOwned) == true)
                    throw new InvalidOperationException("Research replay currently requires native checkout routing.");
                await using (FleetCapacityReservation admission = await new FleetCapacityAdmission(_Database, _Settings)
                    .AcquireAsync(vessel, voyageId, token).ConfigureAwait(false))
                {
                    await admission.VerifyOwnershipAsync(token).ConfigureAwait(false);
                    request.CapturedConfigurationDigest = await CaptureConfigurationDigestAsync(actor, vessel, token).ConfigureAwait(false);
                    // The database reads and fingerprints every current row again inside its serializable transaction.
                    ResearchReplayPlan applied = await _Database.ResearchReplays.ExecuteAsync(actor.TenantId!, actor.UserId!, voyageId, request, token).ConfigureAwait(false);
                    if (applied.AppliedNow) await NotifyCommittedAsync(applied).ConfigureAwait(false);
                    return applied;
                }
            }
            finally
            {
                for (int index = boundaries.Count - 1; index >= 0; index--) boundaries[index].Dispose();
            }
        }

    }
}
