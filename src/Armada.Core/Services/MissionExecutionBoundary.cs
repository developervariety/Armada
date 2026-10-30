namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Serializes explicit attempt replacement against completion and rejects old launch callbacks.</summary>
    public static class MissionExecutionBoundary
    {
        /// <summary>Shared per-mission boundary; acquire multiple identities in ordinal order.</summary>
        public static KeyedAsyncLock Locks { get; } = new KeyedAsyncLock(StringComparer.Ordinal);

        /// <summary>Serializes launch identity writes with replay. Replay acquires completion boundaries first.</summary>
        public static KeyedAsyncLock LaunchWrites { get; } = new KeyedAsyncLock(StringComparer.Ordinal);

        /// <summary>Hold completion and launch identity stable until deferred output is published.</summary>
        /// <param name="missionId">Mission whose output is being published.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A lease that releases launch ownership before the completion boundary.</returns>
        public static async Task<IDisposable> AcquireOutputAsync(string missionId, CancellationToken token = default)
        {
            IDisposable completion = await Locks.AcquireAsync(missionId, token).ConfigureAwait(false);
            try
            {
                IDisposable launch = await LaunchWrites.AcquireAsync(missionId, token).ConfigureAwait(false);
                return new OutputLease(completion, launch);
            }
            catch
            {
                completion.Dispose();
                throw;
            }
        }

        /// <summary>Whether a completion callback still belongs to the current replayed native launch.</summary>
        /// <param name="mission">Current durable mission.</param>
        /// <param name="captain">Captain snapshot carried by the completion callback.</param>
        /// <returns>False for an old callback after a replay, including while the new attempt is Pending.</returns>
        public static bool OwnsReplayLaunch(Mission? mission, Captain captain)
        {
            if (mission == null) return false;
            if (String.IsNullOrEmpty(mission.ResearchReplayId) && mission.GeneratedArtifactReview == null) return true;
            if (mission.GeneratedArtifactReview != null
                && (!mission.StartedUtc.HasValue || mission.StartedUtc.Value < mission.GeneratedArtifactReview.RequestedUtc)) return false;
            return mission.StartedUtc.HasValue && !String.IsNullOrEmpty(mission.DockId)
                && mission.CaptainId == captain.Id && captain.CurrentMissionId == mission.Id
                && captain.CurrentDockId == mission.DockId;
        }
        /// <summary>Identify the durable attempt whose deferred output and process writes are still current.</summary>
        public static string GenerationOf(Mission? mission)
        {
            if (mission == null) return String.Empty;
            GeneratedArtifactReviewBinding? review = mission.GeneratedArtifactReview;
            return review == null ? mission.ResearchReplayId ?? String.Empty
                : (mission.ResearchReplayId ?? String.Empty) + ":generated:" + review.PreparationAttempt
                    + ":" + review.CandidateCommit;
        }

        private sealed class OutputLease : IDisposable
        {
            private readonly IDisposable _Completion;
            private readonly IDisposable _Launch;
            private int _Released;

            public OutputLease(IDisposable completion, IDisposable launch)
            {
                _Completion = completion;
                _Launch = launch;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _Released, 1) != 0) return;
                _Launch.Dispose();
                _Completion.Dispose();
            }
        }
    }
}
