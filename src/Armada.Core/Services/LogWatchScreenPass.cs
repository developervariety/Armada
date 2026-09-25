namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The model screening pass: it asks the D8 <c>log_watch</c> decision whether a still-running
    /// captain's log has gone off course against the brief the log states, and reports the drift class
    /// it is told. It is the judgement half of the screen, beside the deterministic pass, and it
    /// reports only classes a rule cannot settle.
    ///
    /// The pass reads nothing of its own: it receives the bounded tail the host already read, makes at
    /// most one decision call for it, and returns findings. It never writes to a mission, a voyage, a
    /// captain, a dock, or a check, and it cannot cancel, pause, mail, re-dispatch, or steer anything.
    /// Below the decision threshold, with the decision Off, or with the provider unavailable, it
    /// reports nothing, so the screen stays silent rather than guessing.
    /// </summary>
    public sealed class LogWatchScreenPass : ICaptainLogScreenPass
    {
        #region Public-Members

        /// <inheritdoc />
        public string Name => "log_watch";

        #endregion

        #region Private-Members

        // The evidence line the board note carries: the last line of the tail that holds content, which
        // is what the captain was doing when the reading was taken.
        private const int _MaxEvidenceLength = 240;

        // At or above this the drift is reported as still correctable. Below it, the note says a
        // correction would no longer change the outcome, so an operator does not spend a note on it.
        private const double _CorrectableFloor = 0.5;

        private readonly TypedLogWatchAdapter _Adapter;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="adapter">The D8 adapter. It holds the decision's mode and threshold, so the
        /// pass needs no switch of its own: an Off decision makes no call and reports nothing.</param>
        public LogWatchScreenPass(TypedLogWatchAdapter adapter)
        {
            _Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<IReadOnlyList<LogScreenFinding>> EvaluateAsync(LogScreenContext context, CancellationToken token)
        {
            List<LogScreenFinding> findings = new List<LogScreenFinding>();
            if (context == null || String.IsNullOrWhiteSpace(context.Tail)) return findings;

            // A tail whose final outcome is [ARMADA:RESULT] BLOCKED has stated its block through the one
            // blocked-result rule, and the completion path fails the stage and raises the question to the
            // owner. The captain has stopped, so there is no course left to read, and a blocked_unstated
            // reading would contradict the log. No decision call is made.
            if (CaptainBlockedResult.IsBlocked(context.Tail)) return findings;

            LogWatchDecisionInput input = new LogWatchDecisionInput
            {
                MissionId = context.MissionId,
                VoyageId = context.VoyageId,
                VesselId = context.VesselId,
                CaptainId = context.CaptainId,
                LogTail = context.Tail
            };

            // Exactly one decision call per evaluation, and the adapter is contracted never to throw:
            // a timeout, a non-2xx, or a parse error returns the on-track rule verdict and records an
            // unavailable event, so the host sees a clean tail rather than an exception.
            LogWatchVerdict verdict = await _Adapter
                .DecideAsync(input, LogWatchVerdict.OnTrack(), token).ConfigureAwait(false);

            if (!verdict.IsDrift) return findings;

            findings.Add(new LogScreenFinding
            {
                RuleClass = verdict.DriftClass,
                EvidenceLine = LastContentLine(context.Tail),
                Source = LogScreenFinding.SourceModel,
                Confidence = verdict.Confidence,
                Detail = BuildDetail(verdict)
            });
            return findings;
        }

        #endregion

        #region Private-Methods

        private static string BuildDetail(LogWatchVerdict verdict)
        {
            string correctable = verdict.CorrectableNow >= _CorrectableFloor
                ? "A correction to the next stage's brief, or an operator note now, would change the outcome."
                : "The drift is already past the point a note would help.";
            return "The model read the running log as " + verdict.DriftClass + " (confidence "
                + verdict.Confidence.ToString("0.00", CultureInfo.InvariantCulture) + "). " + correctable
                + " Advisory only: correcting or halting the mission stays an operator action.";
        }

        /// <summary>
        /// The last line of the tail that holds content. It is what the captain was doing when the
        /// reading was taken, which is the one line of evidence the board note carries.
        /// </summary>
        private static string LastContentLine(string tail)
        {
            string[] lines = tail.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                return line.Length > _MaxEvidenceLength ? line.Substring(0, _MaxEvidenceLength) : line;
            }
            return String.Empty;
        }

        #endregion
    }
}
