namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The one walker that cancels the stages stranded behind a mission that will never produce work: a failed
    /// mission whose voyage stays alive, and a mission an operator cancels.
    /// </summary>
    public static class MissionDependentCancellation
    {
        /// <summary>
        /// Cancel every mission of the root's voyage that waits, directly or through other stages, on the root.
        /// <para>
        /// The walk follows the whole downstream chain, not only its first link: a cancelled dependent is itself a
        /// dependency, so stopping after one level would leave every later stage Pending for ever while its voyage
        /// still reads live. A stage that already produced work (Complete, PullRequestOpen, WorkProduced) is not
        /// blocked, and neither is anything behind it. A stage already Failed, Cancelled or LandingFailed is left as
        /// it is, but the walk continues through it, because its own dependents are still stranded.
        /// </para>
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="root">The mission that will not produce work.</param>
        /// <param name="failureReason">Failure reason recorded on each cancelled dependent.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The dependents this call cancelled, in walk order.</returns>
        public static async Task<List<Mission>> CancelBlockedAsync(
            DatabaseDriver database,
            Mission root,
            string failureReason,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            List<Mission> cancelled = new List<Mission>();
            if (root == null || String.IsNullOrEmpty(root.VoyageId)) return cancelled;

            List<Mission> voyageMissions = await database.Missions.EnumerateByVoyageAsync(root.VoyageId, token).ConfigureAwait(false);
            HashSet<string> blocked = new HashSet<string>(StringComparer.Ordinal) { root.Id };
            bool discoveredMore = true;

            while (discoveredMore)
            {
                discoveredMore = false;

                foreach (Mission dependent in voyageMissions)
                {
                    if (dependent == null) continue;
                    if (blocked.Contains(dependent.Id)) continue;
                    if (String.IsNullOrEmpty(dependent.DependsOnMissionId)) continue;
                    if (!blocked.Contains(dependent.DependsOnMissionId!)) continue;

                    bool hasProducedWork =
                        dependent.Status == MissionStatusEnum.Complete ||
                        dependent.Status == MissionStatusEnum.PullRequestOpen ||
                        dependent.Status == MissionStatusEnum.WorkProduced;
                    if (hasProducedWork) continue;

                    blocked.Add(dependent.Id);
                    discoveredMore = true;

                    if (dependent.Status == MissionStatusEnum.Failed ||
                        dependent.Status == MissionStatusEnum.Cancelled ||
                        dependent.Status == MissionStatusEnum.LandingFailed) continue;

                    dependent.Status = MissionStatusEnum.Cancelled;
                    dependent.FailureReason = failureReason;
                    dependent.ProcessId = null;
                    dependent.CompletedUtc = DateTime.UtcNow;
                    dependent.LastUpdateUtc = DateTime.UtcNow;
                    Mission stored = await database.Missions.UpdateAsync(dependent, token).ConfigureAwait(false);
                    cancelled.Add(stored);
                }
            }

            return cancelled;
        }
    }
}
