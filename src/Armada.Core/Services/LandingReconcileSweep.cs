namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Finds LandingFailed missions whose work is already on the vessel's remote default branch and proposes each to the
    /// operator. A landing that failed for a reason that later cleared, and was then published by another path, stays
    /// LandingFailed until someone notices; the sweep notices. It never changes a mission: the proposal is an event
    /// that raises one Needs You item, and the operator's confirmation completes the mission after the manual
    /// completion proof passes. The same sweep reads the same proof for Failed merge entries and stores it on the entry
    /// as its delivery evidence, so the inbox shows a failed merge whose work is already delivered as context and not as
    /// a failure; the entry keeps its Failed outcome.
    /// </summary>
    /// <remarks>
    /// Each sweep reads one bounded page of LandingFailed missions, oldest first, and the next sweep reads the next page,
    /// wrapping after the last, so a page of missions that can never be proven does not starve the rest. Each vessel is
    /// fetched at most once per sweep. A vessel whose repository cannot be fetched is skipped, never judged on stale refs.
    /// </remarks>
    public sealed class LandingReconcileSweep
    {
        #region Public-Members

        /// <summary>
        /// Maximum number of LandingFailed missions examined per sweep (1 to 1000, default 25).
        /// </summary>
        public int MaxPerCycle
        {
            get => _MaxPerCycle;
            set
            {
                if (value < 1 || value > 1000) throw new ArgumentOutOfRangeException(nameof(MaxPerCycle), "MaxPerCycle must be between 1 and 1000.");
                _MaxPerCycle = value;
            }
        }

        #endregion

        #region Private-Members

        private readonly string _Header = "[LandingReconcileSweep] ";
        private readonly DatabaseDriver _Database;
        private readonly IGitService _Git;
        private readonly LoggingModule _Logging;
        private readonly LandingReconcileProof _Proof;
        private readonly SemaphoreSlim _Gate = new SemaphoreSlim(1, 1);
        private int _MaxPerCycle = 25;
        private int _NextPage = 1;
        private int _NextMergeOffset = 0;

        /// <summary>
        /// Event type recorded once per Failed merge entry when the sweep finds proof that its work is on the target branch.
        /// </summary>
        public const string MergeDeliveryProvenEventType = "merge.delivery_proven";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="git">Git service; a Harbor-aware service reads a Harbor vessel on its Harbor.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="logDirectory">Server log directory that holds the stage start records, or null when none is kept.</param>
        public LandingReconcileSweep(DatabaseDriver database, IGitService git, LoggingModule logging, string? logDirectory = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Proof = new LandingReconcileProof(database, git, logging, logDirectory);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run one sweep: examine the next page of LandingFailed missions and propose each whose work is proven to be on
        /// its vessel's remote default branch and that has no proposal yet.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of missions proposed in this sweep.</returns>
        public async Task<int> SweepAsync(CancellationToken token = default)
        {
            if (!await _Gate.WaitAsync(0, token).ConfigureAwait(false)) return 0;
            try
            {
                List<MissionSummary> batch = await ReadBatchAsync(token).ConfigureAwait(false);
                Dictionary<string, Vessel?> fetched = new Dictionary<string, Vessel?>(StringComparer.Ordinal);
                int proposed = 0;
                foreach (MissionSummary summary in batch)
                {
                    token.ThrowIfCancellationRequested();
                    if (String.IsNullOrEmpty(summary.VesselId)) continue;

                    try
                    {
                        if (await ProposeAsync(summary, fetched, token).ConfigureAwait(false)) proposed++;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
                    {
                        _Logging.Warn(_Header + "error checking mission " + summary.Id + ": " + ex.Message);
                    }
                }

                await SweepFailedMergesAsync(fetched, token).ConfigureAwait(false);
                return proposed;
            }
            finally
            {
                _Gate.Release();
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Examine the next page of Failed merge entries that have no delivery evidence and store the proof on each one
        /// whose mission work is on its target branch. The page rotates like the LandingFailed page.
        /// </summary>
        private async Task SweepFailedMergesAsync(Dictionary<string, Vessel?> fetched, CancellationToken token)
        {
            List<MergeEntry> failed = await _Database.MergeEntries.EnumerateByStatusAsync(MergeStatusEnum.Failed, token).ConfigureAwait(false);
            List<MergeEntry> unproven = failed
                .Where(entry => entry.DeliveryEvidence == null && !String.IsNullOrEmpty(entry.MissionId) && !String.IsNullOrEmpty(entry.VesselId))
                .OrderBy(entry => entry.CreatedUtc)
                .ToList();
            if (unproven.Count == 0)
            {
                _NextMergeOffset = 0;
                return;
            }

            int start = _NextMergeOffset >= unproven.Count ? 0 : _NextMergeOffset;
            int take = Math.Min(_MaxPerCycle, unproven.Count);
            _NextMergeOffset = start + take >= unproven.Count ? 0 : start + take;
            for (int i = 0; i < take; i++)
            {
                token.ThrowIfCancellationRequested();
                MergeEntry entry = unproven[(start + i) % unproven.Count];
                try
                {
                    await StoreMergeDeliveryAsync(entry, fetched, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
                {
                    _Logging.Warn(_Header + "error checking failed merge entry " + entry.Id + ": " + ex.Message);
                }
            }
        }

        private async Task StoreMergeDeliveryAsync(MergeEntry entry, Dictionary<string, Vessel?> fetched, CancellationToken token)
        {
            Mission? mission = await _Database.Missions.ReadAsync(entry.MissionId!, token).ConfigureAwait(false);
            if (mission == null) return;

            Vessel? vessel = await FetchVesselOnceAsync(entry.VesselId!, fetched, token).ConfigureAwait(false);
            if (vessel == null) return;

            LandingReconcileEvidence? evidence = await _Proof.ProveDeliveredAsync(mission, vessel, null, entry.TargetBranch, token).ConfigureAwait(false);
            if (evidence == null) return;

            // Write onto the current row, and only while the entry is still a failure.
            MergeEntry? current = await _Database.MergeEntries.ReadAsync(entry.Id, token).ConfigureAwait(false);
            if (current == null || current.Status != MergeStatusEnum.Failed || current.DeliveryEvidence != null) return;
            current.DeliveryEvidence = evidence;
            await _Database.MergeEntries.UpdateAsync(current, token).ConfigureAwait(false);

            ArmadaEvent proven = new ArmadaEvent(MergeDeliveryProvenEventType,
                "Failed merge " + current.Id + " carries work already on " + evidence.TargetRef + ": " + evidence.Describe());
            proven.EntityType = "merge_entry";
            proven.EntityId = current.Id;
            proven.MissionId = mission.Id;
            proven.VesselId = current.VesselId;
            proven.VoyageId = mission.VoyageId;
            EventOwnerScope.ApplyFromMission(proven, mission);
            await _Database.Events.CreateAsync(proven, token).ConfigureAwait(false);
            _Logging.Info(_Header + "failed merge entry " + current.Id + " carries work already delivered: " + evidence.Describe());
        }

        private async Task<List<MissionSummary>> ReadBatchAsync(CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                Status = MissionStatusEnum.LandingFailed.ToString(),
                PageSize = _MaxPerCycle,
                PageNumber = _NextPage,
                Order = EnumerationOrderEnum.CreatedAscending
            };

            EnumerationResult<MissionSummary> page = await _Database.Missions.EnumerateMissionSummariesAsync(query, token).ConfigureAwait(false);
            if (page.Objects.Count == 0 && query.PageNumber > 1)
            {
                query.PageNumber = 1;
                page = await _Database.Missions.EnumerateMissionSummariesAsync(query, token).ConfigureAwait(false);
            }

            _NextPage = page.TotalPages > query.PageNumber ? query.PageNumber + 1 : 1;
            return page.Objects;
        }

        private async Task<bool> ProposeAsync(MissionSummary summary, Dictionary<string, Vessel?> fetched, CancellationToken token)
        {
            Mission? mission = await _Database.Missions.ReadAsync(summary.Id, token).ConfigureAwait(false);
            if (mission == null || mission.Status != MissionStatusEnum.LandingFailed) return false;
            if (LandingRefusalReasons.IsRefusal(mission.FailureReason)) return false;
            if (await LandingReconcileProposals.ReadAsync(_Database, mission.Id, token).ConfigureAwait(false) != null) return false;

            Vessel? vessel = await FetchVesselOnceAsync(summary.VesselId!, fetched, token).ConfigureAwait(false);
            if (vessel == null) return false;

            LandingReconcileEvidence? evidence = await _Proof.ProveAsync(mission, vessel, token).ConfigureAwait(false);
            if (evidence == null) return false;

            await LandingReconcileProposals.RecordAsync(_Database, mission, evidence, token).ConfigureAwait(false);
            _Logging.Info(_Header + "mission " + mission.Id + " is LandingFailed but proven landed; proposed for operator confirmation: " + evidence.Describe());
            return true;
        }

        private async Task<Vessel?> FetchVesselOnceAsync(string vesselId, Dictionary<string, Vessel?> fetched, CancellationToken token)
        {
            if (fetched.TryGetValue(vesselId, out Vessel? known)) return known;

            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null || String.IsNullOrWhiteSpace(vessel.LocalPath))
            {
                _Logging.Debug(_Header + "vessel " + vesselId + " has no repository to check");
                fetched[vesselId] = null;
                return null;
            }

            // Fetch once per vessel per sweep so origin/<branch> reflects the remote. A repository that cannot be
            // fetched (a dropped Harbor, an unreachable remote) is skipped rather than judged on stale refs.
            try
            {
                await _Git.FetchAsync(vessel.LocalPath, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                _Logging.Debug(_Header + "fetch failed for vessel " + vesselId + "; its missions are not checked: " + ex.Message);
                fetched[vesselId] = null;
                return null;
            }

            fetched[vesselId] = vessel;
            return vessel;
        }

        #endregion
    }
}
