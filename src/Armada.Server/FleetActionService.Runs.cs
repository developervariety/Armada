namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class FleetActionService
    {
        /// <summary>Create or replay one actor-scoped durable run request.</summary>
        public async Task<FleetActionRun> StartRunAsync(AuthContext caller, string actionId, FleetActionRunRequest request, CancellationToken token = default)
        {
            AuthContext actor = await ValidateOperatorAsync(caller, token).ConfigureAwait(false);
            ValidateRunRequest(actionId, request);
            string fingerprint = FleetActionSnapshotBuilder.Fingerprint(actionId, request.VesselIds);
            FleetActionRun? replay = await _Database.FleetActionRuns.ReadByRequestIdAsync(
                actor.TenantId!, actor.UserId!, request.RequestId, token).ConfigureAwait(false);
            if (replay != null) return RequireMatchingReplay(replay, fingerprint);

            FleetAction? action = await _Database.FleetActions.ReadAsync(actor.TenantId!, actor.UserId!, actionId, token).ConfigureAwait(false);
            if (action == null) throw new KeyNotFoundException("Fleet action was not found.");
            if (!action.Active) throw new InvalidOperationException("The fleet action is inactive.");
            FleetActionRun run = await FleetActionSnapshotBuilder.BuildAsync(
                _Database, _Admiral, actor, action, request.RequestId, request.VesselIds, fingerprint, token).ConfigureAwait(false);
            FleetActionRun saved = await _Database.FleetActionRuns.CreateOrReadAsync(run, token).ConfigureAwait(false);
            return RequireMatchingReplay(saved, fingerprint);
        }

        /// <summary>Read one run for the exact initiating operator.</summary>
        public async Task<FleetActionRun?> GetRunAsync(AuthContext caller, string runId, CancellationToken token = default)
        {
            AuthContext actor = await ValidateOperatorAsync(caller, token).ConfigureAwait(false);
            return await _Database.FleetActionRuns.ReadAsync(actor.TenantId!, actor.UserId!, runId, token).ConfigureAwait(false);
        }

        /// <summary>List saved runs for the exact initiating operator.</summary>
        public async Task<List<FleetActionRun>> ListRunsAsync(AuthContext caller, int limit = 100, CancellationToken token = default)
        {
            AuthContext actor = await ValidateOperatorAsync(caller, token).ConfigureAwait(false);
            int bounded = Math.Max(1, Math.Min(200, limit));
            return await _Database.FleetActionRuns.EnumerateAsync(actor.TenantId!, actor.UserId!, bounded, token).ConfigureAwait(false);
        }

        /// <summary>Request cancellation of all not-yet-terminal targets in one owned run.</summary>
        public async Task<FleetActionRun> CancelRunAsync(AuthContext caller, string runId, CancellationToken token = default)
        {
            AuthContext actor = await ValidateOperatorAsync(caller, token).ConfigureAwait(false);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                FleetActionRun? run = await _Database.FleetActionRuns.ReadAsync(actor.TenantId!, actor.UserId!, runId, token).ConfigureAwait(false);
                if (run == null) throw new KeyNotFoundException("Fleet action run was not found.");
                FleetActionRunState next = FleetActionRunStateBuilder.Clone(run.State);
                foreach (FleetActionRunTargetState target in next.TargetStates)
                {
                    if (target.Status == Armada.Core.Enums.FleetActionTargetStatusEnum.Pending)
                        FleetActionRunStateBuilder.SetTarget(target, Armada.Core.Enums.FleetActionTargetStatusEnum.Cancelled, "Cancelled by operator.");
                    else if (target.Status == Armada.Core.Enums.FleetActionTargetStatusEnum.Dispatching
                        || target.Status == Armada.Core.Enums.FleetActionTargetStatusEnum.Running)
                        target.CancelRequested = true;
                }
                FleetActionRunStateBuilder.Recalculate(next);
                FleetActionRun? saved = await _Database.FleetActionRuns.TryCompareExchangeStateAsync(
                    run.TenantId, run.UserId, run.Id, run.Revision, next, token).ConfigureAwait(false);
                if (saved != null) return saved;
            }
            throw new InvalidOperationException("The fleet action run changed during cancellation. Try again.");
        }

        private static FleetActionRun RequireMatchingReplay(FleetActionRun run, string fingerprint)
        {
            if (!String.Equals(run.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The request ID already names a different fleet action run.");
            return run;
        }

        private static void ValidateRunRequest(string actionId, FleetActionRunRequest request)
        {
            if (String.IsNullOrWhiteSpace(actionId) || actionId.Length > 128 || actionId != actionId.Trim())
                throw new ArgumentException("A canonical action ID is required.", nameof(actionId));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > _MaximumRequestIdLength)
                throw new ArgumentException("A request ID of at most 128 characters is required.", nameof(request));
            if (request.VesselIds == null || request.VesselIds.Count == 0 || request.VesselIds.Count > _MaximumTargets)
                throw new ArgumentException("A run must select between one and fifty vessels.", nameof(request));
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string vesselId in request.VesselIds)
            {
                if (String.IsNullOrWhiteSpace(vesselId) || vesselId.Length > 128 || vesselId != vesselId.Trim() || !unique.Add(vesselId))
                    throw new ArgumentException("Vessel IDs must be non-empty and unique.", nameof(request));
            }
        }
    }
}
