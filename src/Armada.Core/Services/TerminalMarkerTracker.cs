namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    /// <summary>
    /// In-memory record of the first stage-ending marker each running mission emitted.
    /// </summary>
    /// <remarks>
    /// A captain that prints its terminal marker and keeps its process running is finished, but it
    /// looks exactly like a captain still working: Armada otherwise reads the verdict only when the
    /// process exits. A stall nudge sent to such a captain asks a finished reviewer for more work,
    /// and it answers by reviewing again. The tracker is the one place that knows a mission's
    /// output already carries its terminal marker, so the lifecycle handler can complete the stage
    /// after a bounded grace period and the recovery orchestrator can withhold its nudge.
    /// Runtime state only: a restart clears it, and the process-exit path still reads the marker
    /// from the mission output.
    /// </remarks>
    public sealed class TerminalMarkerTracker
    {
        #region Private-Members

        private readonly ConcurrentDictionary<string, TerminalMarkerRecord> _FirstMarkers =
            new ConcurrentDictionary<string, TerminalMarkerRecord>(StringComparer.Ordinal);

        private long _SuppressedNudges = 0;

        #endregion

        #region Public-Members

        /// <summary>
        /// Number of stall nudges withheld because the mission had already emitted its terminal marker.
        /// </summary>
        public long SuppressedNudgeCount => Interlocked.Read(ref _SuppressedNudges);

        #endregion

        #region Public-Methods

        /// <summary>
        /// The one definition of a terminal marker: <c>[ARMADA:VERDICT] PASS|FAIL|NEEDS_REVISION</c>
        /// or <c>[ARMADA:RESULT] COMPLETE</c>, as parsed by <see cref="ProgressParser"/>.
        /// </summary>
        /// <param name="signal">Parsed progress signal.</param>
        /// <returns>True when the signal ends the captain's stage.</returns>
        public static bool IsTerminalMarker(ProgressParser.ProgressSignal? signal)
        {
            if (signal == null || String.IsNullOrWhiteSpace(signal.Value)) return false;
            string value = signal.Value.Trim();

            if (String.Equals(signal.Type, "verdict", StringComparison.OrdinalIgnoreCase))
            {
                return String.Equals(value, "PASS", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(value, "FAIL", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(value, "NEEDS_REVISION", StringComparison.OrdinalIgnoreCase);
            }

            if (String.Equals(signal.Type, "result", StringComparison.OrdinalIgnoreCase))
            {
                return String.Equals(value, "COMPLETE", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// True when the signal ends the captain's stage: a terminal marker
        /// (<see cref="IsTerminalMarker"/>) or <c>[ARMADA:RESULT] BLOCKED</c>. A blocked stage makes no
        /// completion claim, but it is finished: a captain that waits after it would otherwise be nudged
        /// to continue without the owner's answer. The completion path still reads the final outcome and
        /// fails the stage with its question (<see cref="CaptainBlockedResult"/>).
        /// </summary>
        /// <param name="signal">Parsed progress signal.</param>
        /// <returns>True when the signal ends the captain's stage.</returns>
        public static bool IsStageEndingMarker(ProgressParser.ProgressSignal? signal)
        {
            if (IsTerminalMarker(signal)) return true;
            if (signal == null) return false;
            return String.Equals(signal.Type, "result", StringComparison.OrdinalIgnoreCase)
                && ProgressParser.ValueStartsWithWord(signal.Value?.Trim(), CaptainBlockedResult.BlockedWord);
        }

        /// <summary>
        /// Record a stage-ending marker (<see cref="IsStageEndingMarker"/>) for a mission. Only the first
        /// marker is kept; later markers (for example a re-review) never replace it.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="signal">Parsed terminal marker.</param>
        /// <param name="utc">UTC instant the marker was seen.</param>
        /// <returns>True when this call recorded the mission's first terminal marker.</returns>
        public bool TryRecordFirst(string missionId, ProgressParser.ProgressSignal signal, DateTime utc)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            if (!IsStageEndingMarker(signal)) return false;

            DateTime normalized = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
            // A blocked marker may carry the captain's question; the record keeps only the marker word.
            string value = IsTerminalMarker(signal) ? signal.Value.Trim().ToUpperInvariant() : CaptainBlockedResult.BlockedWord;
            TerminalMarkerRecord record = new TerminalMarkerRecord(
                missionId,
                signal.Type.ToLowerInvariant(),
                value,
                normalized);
            return _FirstMarkers.TryAdd(missionId, record);
        }

        /// <summary>
        /// Look up the first terminal marker a mission emitted.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="record">The first marker, or null when none was recorded.</param>
        /// <returns>True when a marker was recorded.</returns>
        public bool TryGet(string missionId, out TerminalMarkerRecord? record)
        {
            record = null;
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            if (!_FirstMarkers.TryGetValue(missionId, out TerminalMarkerRecord? found)) return false;
            record = found;
            return true;
        }

        /// <summary>
        /// Count one withheld stall nudge.
        /// </summary>
        /// <returns>The running total of withheld nudges.</returns>
        public long RecordSuppressedNudge()
        {
            return Interlocked.Increment(ref _SuppressedNudges);
        }

        /// <summary>
        /// Forget a mission's marker once its process has exited.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        public void Clear(string missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return;
            _FirstMarkers.TryRemove(missionId, out TerminalMarkerRecord? _);
        }

        #endregion
    }
}
