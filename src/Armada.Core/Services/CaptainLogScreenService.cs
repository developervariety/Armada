namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// The cadence host of the read-only captain-log screen. Each sweep enumerates the in-progress
    /// missions, reads a bounded tail of each mission's live log, runs every registered pass over
    /// that tail, and on a finding posts one voyage-tagged board note and emits one event.
    ///
    /// The screen never cancels, pauses, mails, re-dispatches, kills, or steers a mission: its only
    /// writes are the board note and the event. A mission whose screen fails is logged with the
    /// reason and the sweep continues, so one mission can never stop the others being screened.
    /// </summary>
    public class CaptainLogScreenService
    {
        #region Public-Members

        /// <summary>
        /// Event type written once per screened mission, whether or not anything was found. The
        /// payload carries the outcome and the per-class counts, so an operator reads the counts
        /// over a date range by enumerating this one event type and summing the payloads.
        /// </summary>
        public const string ScreenEventType = "captain.log_screen";

        /// <summary>Payload outcome for a screen that produced findings.</summary>
        public const string OutcomeFlagged = "flagged";

        /// <summary>
        /// Payload outcome for a screen that ran every pass and found nothing. A clean screen is
        /// recorded, so it is distinguishable from a screen that never ran, which writes no event.
        /// </summary>
        public const string OutcomeClean = "clean";

        #endregion

        #region Private-Members

        private const string _Header = "[CaptainLogScreenService] ";
        private const string _NoteHeading = "Captain log screen";

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly CaptainLogScreeningSettings _Settings;
        private readonly ICaptainLogTailReader _TailReader;
        private readonly IVoyageTaggedNotePoster? _NotePoster;
        private readonly List<ICaptainLogScreenPass> _Passes;
        private readonly Func<DateTime> _UtcNow;

        // Keyed by mission identifier, the same shape the escalation evaluator uses for its own
        // per-entity cooldowns: a flagged mission is not re-flagged until its window expires.
        private readonly ConcurrentDictionary<string, DateTime> _Cooldowns = new ConcurrentDictionary<string, DateTime>();

        // Keyed by mission identifier, the hash of the tail the last screen read. An unchanged tail
        // costs no pass call at all.
        private readonly ConcurrentDictionary<string, string> _LastTailHashes = new ConcurrentDictionary<string, string>();

        private DateTime? _LastSweepUtc = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Screening settings, held by reference so a hot reload reaches the screen.</param>
        /// <param name="tailReader">Bounded log-tail reader.</param>
        /// <param name="passes">The screening passes to run over each tail. An empty set screens nothing.</param>
        /// <param name="notePoster">Optional voyage-tagged note poster. Without one the screen still records its outcomes.</param>
        /// <param name="utcNow">Optional clock, for tests.</param>
        public CaptainLogScreenService(
            LoggingModule logging,
            DatabaseDriver database,
            CaptainLogScreeningSettings settings,
            ICaptainLogTailReader tailReader,
            IEnumerable<ICaptainLogScreenPass> passes,
            IVoyageTaggedNotePoster? notePoster = null,
            Func<DateTime>? utcNow = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _TailReader = tailReader ?? throw new ArgumentNullException(nameof(tailReader));
            _Passes = (passes ?? throw new ArgumentNullException(nameof(passes))).Where(p => p != null).ToList();
            _NotePoster = notePoster;
            _UtcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run one sweep. The cadence host may call this every cycle: a call made inside the
        /// configured interval, or while the screen is off, returns the naming outcome without
        /// reading any log. The method never throws into its host.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What the sweep did. Never null.</returns>
        public async Task<CaptainLogScreenSweepResult> SweepAsync(CancellationToken token = default)
        {
            try
            {
                return await SweepInnerAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "sweep failed: " + ex.Message);
                return new CaptainLogScreenSweepResult
                {
                    Outcome = CaptainLogScreenSweepOutcomeEnum.Failed,
                    Reason = ex.GetType().Name + ": " + ex.Message
                };
            }
        }

        #endregion

        #region Private-Methods

        private async Task<CaptainLogScreenSweepResult> SweepInnerAsync(CancellationToken token)
        {
            if (!_Settings.Enabled)
                return Skipped(CaptainLogScreenSweepOutcomeEnum.Disabled, "captainLogScreening.enabled is false");

            DateTime now = _UtcNow();
            if (_LastSweepUtc.HasValue && (now - _LastSweepUtc.Value).TotalSeconds < _Settings.IntervalSeconds)
                return Skipped(CaptainLogScreenSweepOutcomeEnum.IntervalNotElapsed,
                    "interval of " + _Settings.IntervalSeconds + "s has not elapsed");

            if (_Passes.Count == 0)
                return Skipped(CaptainLogScreenSweepOutcomeEnum.NoPasses, "no screening passes are registered");

            _LastSweepUtc = now;

            List<Mission> active = await _Database.Missions
                .EnumerateByStatusAsync(MissionStatusEnum.InProgress, token).ConfigureAwait(false);

            if (active.Count == 0)
                return Skipped(CaptainLogScreenSweepOutcomeEnum.NoActiveMissions, "no in-progress missions");

            CaptainLogScreenSweepResult result = new CaptainLogScreenSweepResult
            {
                Outcome = CaptainLogScreenSweepOutcomeEnum.Completed
            };

            foreach (Mission mission in active)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await ScreenMissionAsync(mission, now, result, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One mission's failure must not stop the others being screened, and a swallowed
                    // condition must still say why it happened.
                    result.Errors++;
                    _Logging.Warn(_Header + "screen failed for mission " + mission.Id + ": "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }

            _Logging.Debug(_Header + "sweep screened=" + result.Screened + " flagged=" + result.Flagged
                + " clean=" + result.Clean + " unchanged=" + result.UnchangedTails
                + " cooldown=" + result.InCooldown + " noLog=" + result.NoLog + " errors=" + result.Errors);
            return result;
        }

        private async Task ScreenMissionAsync(Mission mission, DateTime now, CaptainLogScreenSweepResult result, CancellationToken token)
        {
            if (_Cooldowns.TryGetValue(mission.Id, out DateTime flaggedAt)
                && (now - flaggedAt).TotalMinutes < _Settings.CooldownMinutes)
            {
                result.InCooldown++;
                return;
            }

            string? tail = await _TailReader.ReadTailAsync(mission, _Settings.TailLines, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(tail))
            {
                result.NoLog++;
                return;
            }

            string hash = Sha256Hex(tail!);
            if (_LastTailHashes.TryGetValue(mission.Id, out string? previous) && previous == hash)
            {
                // The tail has not changed since the last screen, so no pass is called and no record
                // is written: the previous screen's outcome still describes this content.
                result.UnchangedTails++;
                return;
            }

            LogScreenContext context = new LogScreenContext
            {
                MissionId = mission.Id,
                VoyageId = mission.VoyageId,
                CaptainId = mission.CaptainId,
                Tail = tail!,
                TailSha256 = hash,
                TailBytes = Encoding.UTF8.GetByteCount(tail!)
            };

            List<LogScreenFinding> findings = new List<LogScreenFinding>();
            foreach (ICaptainLogScreenPass pass in _Passes)
            {
                IReadOnlyList<LogScreenFinding>? passFindings =
                    await pass.EvaluateAsync(context, token).ConfigureAwait(false);
                if (passFindings == null) continue;
                foreach (LogScreenFinding finding in passFindings)
                    if (finding != null && !String.IsNullOrWhiteSpace(finding.RuleClass)) findings.Add(finding);
            }

            _LastTailHashes[mission.Id] = hash;
            result.Screened++;

            Dictionary<string, int> counts = CountByClass(findings);
            if (findings.Count == 0)
            {
                result.Clean++;
                await EmitScreenEventAsync(mission, context, OutcomeClean, counts, token).ConfigureAwait(false);
                return;
            }

            result.Flagged++;
            foreach (KeyValuePair<string, int> pair in counts)
                result.FindingsByClass[pair.Key] = result.FindingsByClass.TryGetValue(pair.Key, out int running)
                    ? running + pair.Value
                    : pair.Value;

            await PostNoteAsync(mission, findings, counts, token).ConfigureAwait(false);
            await EmitScreenEventAsync(mission, context, OutcomeFlagged, counts, token).ConfigureAwait(false);

            // Set the cooldown last, so a mission is put on cooldown only once its flag has been
            // written; the same unchanged tail then does not re-flag.
            _Cooldowns[mission.Id] = now;
        }

        private async Task PostNoteAsync(Mission mission, List<LogScreenFinding> findings, Dictionary<string, int> counts, CancellationToken token)
        {
            if (_NotePoster == null) return;

            StringBuilder note = new StringBuilder();
            note.Append(_NoteHeading).Append(": ").Append(String.Join(", ", counts.Keys)).AppendLine();
            note.AppendLine("Advisory only. The screen reads the log and posts this note; it changes nothing on the mission.");
            foreach (string ruleClass in counts.Keys)
            {
                LogScreenFinding first = findings.First(f => f.RuleClass == ruleClass);
                note.Append("- ").Append(ruleClass).Append(" (x").Append(counts[ruleClass]).Append("): ")
                    .Append(first.EvidenceLine).AppendLine();
            }

            await _NotePoster.PostVoyageNoteAsync(
                note.ToString(), mission.VoyageId, mission.Id, mission.VesselId, token).ConfigureAwait(false);
        }

        private async Task EmitScreenEventAsync(Mission mission, LogScreenContext context, string outcome, Dictionary<string, int> counts, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(ScreenEventType,
                    "Captain log screen " + outcome + " for mission " + mission.Id);
                evt.EntityType = "mission";
                evt.EntityId = mission.Id;
                evt.MissionId = mission.Id;
                evt.VoyageId = mission.VoyageId;
                evt.VesselId = mission.VesselId;
                evt.CaptainId = mission.CaptainId;
                CaptainLogScreenEventPayload payload = new CaptainLogScreenEventPayload
                {
                    Outcome = outcome,
                    Counts = counts,
                    Passes = _Passes.Select(p => p.Name).ToList(),
                    TailSha256 = context.TailSha256,
                    TailBytes = context.TailBytes
                };
                evt.Payload = JsonSerializer.Serialize(payload, _JsonOptions);

                EventOwnerScopeResult scope = await EventOwnerScope.ApplyAsync(_Database, evt, token).ConfigureAwait(false);
                if (scope.Outcome == EventOwnerScopeOutcomeEnum.LookupFailed)
                    _Logging.Warn(_Header + "screen event written without owner scope: " + scope.Detail);

                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record screen outcome for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private static Dictionary<string, int> CountByClass(List<LogScreenFinding> findings)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LogScreenFinding finding in findings)
                counts[finding.RuleClass] = counts.TryGetValue(finding.RuleClass, out int running) ? running + 1 : 1;
            return counts;
        }

        private static CaptainLogScreenSweepResult Skipped(CaptainLogScreenSweepOutcomeEnum outcome, string reason)
        {
            return new CaptainLogScreenSweepResult { Outcome = outcome, Reason = reason };
        }

        private static string Sha256Hex(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        #endregion
    }
}
