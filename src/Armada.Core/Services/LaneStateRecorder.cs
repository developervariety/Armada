namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Records shared-lane eligibility, occupancy, and capacity from each scheduler observation. A
    /// row is appended when a lane's state changes, and again as a checkpoint when the last row is
    /// about to leave its trust window, so continuous observation is provable and gaps are visible.
    /// </summary>
    public sealed class LaneStateRecorder
    {
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule? _Logging;
        private readonly Func<DateTime> _Clock;
        private readonly Dictionary<string, LaneStateTransition> _LastWritten = new Dictionary<string, LaneStateTransition>(StringComparer.Ordinal);
        private readonly object _Lock = new object();

        /// <summary>Instantiate.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Optional logger for recording failures.</param>
        /// <param name="clock">Optional UTC clock.</param>
        public LaneStateRecorder(DatabaseDriver database, LoggingModule? logging = null, Func<DateTime>? clock = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging;
            _Clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// The trust window for one observation: twice the longer of the scheduler interval and one
        /// minute. A missed sweep therefore leaves a visible gap instead of extending the last state.
        /// </summary>
        /// <param name="sweepInterval">Scheduler sweep interval.</param>
        /// <returns>Trust window in seconds.</returns>
        public static int TrustWindowSeconds(TimeSpan sweepInterval)
        {
            double seconds = Math.Max(sweepInterval.TotalSeconds, 60) * 2;
            return (int)Math.Min(seconds, 86400);
        }

        /// <summary>
        /// Build one sample per lane that has eligible work or occupancy.
        /// </summary>
        /// <param name="lanes">Lane map.</param>
        /// <param name="eligibleObjectives">Objectives the scheduler considers eligible this sweep.</param>
        /// <param name="occupiedByLaneMember">Function returning the active voyage count for a lane.</param>
        /// <param name="capacity">Per-lane concurrent voyage limit.</param>
        /// <param name="blockReason">Fleet-wide block in force.</param>
        /// <param name="occupiedVesselIds">Vessels that currently carry active voyages.</param>
        /// <returns>Samples keyed by lane.</returns>
        public static List<LaneStateTransition> BuildSamples(
            VesselLaneMap lanes,
            IEnumerable<Objective> eligibleObjectives,
            Func<IReadOnlySet<string>, int> occupiedByLaneMember,
            int capacity,
            LaneBlockReasonEnum blockReason,
            IEnumerable<string> occupiedVesselIds)
        {
            if (lanes == null) throw new ArgumentNullException(nameof(lanes));
            if (eligibleObjectives == null) throw new ArgumentNullException(nameof(eligibleObjectives));
            if (occupiedByLaneMember == null) throw new ArgumentNullException(nameof(occupiedByLaneMember));
            Dictionary<string, LaneStateTransition> samples = new Dictionary<string, LaneStateTransition>(StringComparer.Ordinal);
            Dictionary<string, SortedSet<string>> families = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

            LaneStateTransition SampleFor(string vesselId)
            {
                IReadOnlySet<string> members = lanes.MembersFor(vesselId);
                string key = String.Join("+", members.OrderBy(item => item, StringComparer.Ordinal));
                if (!samples.TryGetValue(key, out LaneStateTransition? sample))
                {
                    sample = new LaneStateTransition
                    {
                        LaneKey = key,
                        Occupied = occupiedByLaneMember(members),
                        Capacity = Math.Max(0, capacity),
                        BlockReason = blockReason
                    };
                    samples[key] = sample;
                    families[key] = new SortedSet<string>(StringComparer.Ordinal);
                }
                return sample;
            }

            foreach (Objective objective in eligibleObjectives)
            {
                // A multi-vessel objective is refused by the scheduler and belongs to no lane.
                if (objective == null || objective.VesselIds.Count != 1) continue;
                LaneStateTransition sample = SampleFor(objective.VesselIds[0]);
                sample.EligibleCount++;
                families[sample.LaneKey].Add(ProductionSourceFamily.Resolve(objective));
            }
            foreach (string vesselId in occupiedVesselIds ?? Enumerable.Empty<string>())
            {
                if (!String.IsNullOrWhiteSpace(vesselId)) SampleFor(vesselId);
            }
            foreach (LaneStateTransition sample in samples.Values)
                sample.EligibleSourceFamilies = String.Join(",", families[sample.LaneKey]);
            return samples.Values.OrderBy(item => item.LaneKey, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Append the rows implied by one observation. Lanes seen before but absent now are written
        /// once as idle and empty, so their previous state does not appear to continue.
        /// </summary>
        /// <param name="samples">Current lane samples.</param>
        /// <param name="validForSeconds">Trust window for this observation.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of rows written.</returns>
        public async Task<int> ObserveAsync(List<LaneStateTransition> samples, int validForSeconds, CancellationToken token = default)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            DateTime now = _Clock();
            List<LaneStateTransition> toWrite = new List<LaneStateTransition>();
            lock (_Lock)
            {
                HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
                foreach (LaneStateTransition sample in samples)
                {
                    present.Add(sample.LaneKey);
                    Stage(sample, validForSeconds, now, toWrite);
                }
                foreach (LaneStateTransition previous in _LastWritten.Values.ToList())
                {
                    if (present.Contains(previous.LaneKey)) continue;
                    if (previous.EligibleCount == 0 && previous.Occupied == 0) continue;
                    Stage(new LaneStateTransition { LaneKey = previous.LaneKey, Capacity = previous.Capacity, BlockReason = LaneBlockReasonEnum.None }, validForSeconds, now, toWrite);
                }
            }

            int written = 0;
            foreach (LaneStateTransition row in toWrite)
            {
                try
                {
                    await _Database.LaneStateTransitions.CreateAsync(row, token).ConfigureAwait(false);
                    written++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lock (_Lock) { _LastWritten.Remove(row.LaneKey); }
                    _Logging?.Warn("[LaneStateRecorder] could not record lane " + row.LaneKey + " (" + ex.GetType().Name + "): " + ex.Message);
                }
            }
            return written;
        }

        private void Stage(LaneStateTransition sample, int validForSeconds, DateTime now, List<LaneStateTransition> toWrite)
        {
            sample.CreatedUtc = now;
            sample.ValidForSeconds = Math.Max(1, validForSeconds);
            if (_LastWritten.TryGetValue(sample.LaneKey, out LaneStateTransition? last))
            {
                bool changed = last.EligibleCount != sample.EligibleCount
                    || last.Occupied != sample.Occupied
                    || last.Capacity != sample.Capacity
                    || last.BlockReason != sample.BlockReason
                    || !String.Equals(last.EligibleSourceFamilies, sample.EligibleSourceFamilies, StringComparison.Ordinal);
                // Checkpoint before half of the last row's trust window has elapsed so a healthy
                // scheduler never leaves an unobserved gap.
                bool due = (now - last.CreatedUtc).TotalSeconds >= last.ValidForSeconds / 2.0;
                if (!changed && !due) return;
                sample.Checkpoint = !changed;
            }
            _LastWritten[sample.LaneKey] = sample;
            toWrite.Add(sample);
        }
    }
}
