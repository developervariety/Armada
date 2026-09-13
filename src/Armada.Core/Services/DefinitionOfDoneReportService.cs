namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Builds the read-only definition-of-done report for a mission from current settings and recorded evaluation events.
    /// It never runs a gate, reads a diff, or infers an outcome from mission status or Check runs.
    /// </summary>
    public class DefinitionOfDoneReportService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly Func<DefinitionOfDoneGate?> _ActiveGate;
        private const string _Header = "[DefinitionOfDoneReportService] ";

        /// <summary>
        /// Skip reason reported when no gate is wired into mission completion.
        /// </summary>
        public const string InactiveGateReason = "definition-of-done gate is not active on this server; completion records no evaluation";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="activeGate">
        /// Returns the gate mission completion actually uses, or null when none is wired. The report describes that gate,
        /// not the current settings file: a settings reload does not replace a gate built at startup.
        /// </param>
        public DefinitionOfDoneReportService(DatabaseDriver database, LoggingModule logging, Func<DefinitionOfDoneGate?> activeGate)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _ActiveGate = activeGate ?? throw new ArgumentNullException(nameof(activeGate));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the report for a mission the caller is already authorized to read.
        /// </summary>
        /// <param name="auth">Caller scope; evaluation events are read in the same scope.</param>
        /// <param name="mission">Mission read in the caller's scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Report with configuration and latest evaluation state.</returns>
        public async Task<MissionDefinitionOfDoneReport> GetForMissionAsync(AuthContext auth, Mission mission, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            MissionDefinitionOfDoneReport report = new MissionDefinitionOfDoneReport { MissionId = mission.Id };

            try
            {
                DefinitionOfDoneGate? gate = _ActiveGate();
                if (gate == null)
                {
                    report.Configuration = new DefinitionOfDoneConfiguration
                    {
                        GateActive = false,
                        Enabled = false,
                        ExpectedSkipReason = InactiveGateReason
                    };
                }
                else
                {
                    report.Configuration = await gate.DescribeAsync(mission, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Configuration = null;
                report.ConfigurationUnavailableReason = "configuration could not be resolved (" + ex.GetType().Name + ")";
                _Logging.Warn(_Header + "could not describe DoD configuration for mission " + mission.Id + ": " + ex.Message);
            }

            await PopulateLatestEvaluationAsync(auth, mission, report, token).ConfigureAwait(false);
            return report;
        }

        #endregion

        #region Private-Methods

        private async Task PopulateLatestEvaluationAsync(AuthContext auth, Mission mission, MissionDefinitionOfDoneReport report, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                MissionId = mission.Id,
                EventType = DefinitionOfDoneEvaluationRecord.EventType,
                Order = EnumerationOrderEnum.CreatedDescending,
                PageNumber = 1,
                PageSize = 2
            };

            List<ArmadaEvent> latest;
            try
            {
                EnumerationResult<ArmadaEvent> result = auth.IsAdmin
                    ? await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false)
                    : auth.IsTenantAdmin
                        ? await _Database.Events.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                        : await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false);
                latest = result.Objects ?? new List<ArmadaEvent>();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetUnavailable(report, "evaluation history could not be read (" + ex.GetType().Name + ")");
                _Logging.Warn(_Header + "could not read DoD evaluation events for mission " + mission.Id + ": " + ex.Message);
                return;
            }

            if (latest.Count == 0)
            {
                report.HistoryState = RecordedHistoryStateEnum.NotRecorded;
                return;
            }

            if (latest.Count > 1 && latest[0].CreatedUtc == latest[1].CreatedUtc)
            {
                SetUnavailable(report, "the latest evaluation records share a timestamp, so their order cannot be determined");
                return;
            }

            DefinitionOfDoneEvaluationRecord? record = ParseRecord(latest[0].Payload, out string? parseFailure);
            if (record == null)
            {
                SetUnavailable(report, parseFailure ?? "the latest evaluation record cannot be read");
                return;
            }

            record.OutputTail = DefinitionOfDoneEvaluationRecord.BoundOutput(record.OutputTail);
            report.HistoryState = RecordedHistoryStateEnum.Recorded;
            report.LatestEvaluation = record;
        }

        /// <summary>
        /// Read a stored payload strictly. Required fields must be present, and enum values must be exact
        /// declared names; model defaults never fill a missing field.
        /// </summary>
        private static DefinitionOfDoneEvaluationRecord? ParseRecord(string? payload, out string? failure)
        {
            failure = null;
            if (String.IsNullOrWhiteSpace(payload))
            {
                failure = "the latest evaluation record has no payload";
                return null;
            }

            if (payload.Length > DefinitionOfDoneEvaluationRecord.MaxStoredPayloadLength)
            {
                failure = "the latest evaluation record is too large to read (" + payload.Length + " characters)";
                return null;
            }

            StoredEvaluationPayload? stored;
            try
            {
                stored = JsonSerializer.Deserialize<StoredEvaluationPayload>(payload);
            }
            catch (JsonException)
            {
                failure = "the latest evaluation record is malformed";
                return null;
            }

            if (stored == null)
            {
                failure = "the latest evaluation record is empty";
                return null;
            }

            if (stored.SchemaVersion == null)
            {
                failure = "the latest evaluation record has no schema version";
                return null;
            }

            if (stored.SchemaVersion.Value != DefinitionOfDoneEvaluationRecord.CurrentSchemaVersion)
            {
                failure = "the latest evaluation record has unsupported schema version " + stored.SchemaVersion.Value;
                return null;
            }

            if (!TryParseExactName(stored.Outcome, out DefinitionOfDoneEvaluationOutcomeEnum outcome))
            {
                failure = "the latest evaluation record has a missing or unknown outcome";
                return null;
            }

            DefinitionOfDoneFailureClassEnum? failureClass = null;
            if (stored.FailureClass != null)
            {
                if (!TryParseExactName(stored.FailureClass, out DefinitionOfDoneFailureClassEnum parsedClass))
                {
                    failure = "the latest evaluation record has an unknown failure class";
                    return null;
                }

                failureClass = parsedClass;
            }

            if (stored.StartedUtc == null || stored.CompletedUtc == null)
            {
                failure = "the latest evaluation record has no evaluation times";
                return null;
            }

            if (stored.RecoveryAttempts < 0)
            {
                failure = "the latest evaluation record has a negative recovery attempt count";
                return null;
            }

            string? contradiction = FindContradiction(outcome, stored);
            if (contradiction != null)
            {
                failure = "the latest evaluation record is " + outcome + " but " + contradiction;
                return null;
            }

            // A reversed StartedUtc/CompletedUtc range is read as recorded: a clock step can produce it on a real
            // evaluation, and hiding that result would be worse than showing its times.
            return new DefinitionOfDoneEvaluationRecord
            {
                SchemaVersion = stored.SchemaVersion.Value,
                Outcome = outcome,
                SkippedReason = DefinitionOfDoneEvaluationRecord.BoundLabel(stored.SkippedReason),
                CommandLabel = DefinitionOfDoneEvaluationRecord.BoundLabel(stored.CommandLabel),
                ExitCode = stored.ExitCode,
                FailureClass = failureClass,
                OutputTail = stored.OutputTail,
                CaptainId = stored.CaptainId,
                DockId = stored.DockId,
                BranchName = stored.BranchName,
                CommitHash = stored.CommitHash,
                RecoveryAttempts = stored.RecoveryAttempts ?? 0,
                StartedUtc = stored.StartedUtc.Value,
                CompletedUtc = stored.CompletedUtc.Value
            };
        }

        /// <summary>
        /// Name a combination of outcome and fields that the record writer never produces, or return null. Passed
        /// carries no skip or failure detail; Skipped and NotVerifiable carry a skipped reason and no failure detail;
        /// Failed and EvaluationError carry a command label and no skipped reason.
        /// </summary>
        private static string? FindContradiction(DefinitionOfDoneEvaluationOutcomeEnum outcome, StoredEvaluationPayload stored)
        {
            bool hasFailureDetail = stored.CommandLabel != null
                || stored.ExitCode != null
                || stored.FailureClass != null
                || stored.OutputTail != null;

            switch (outcome)
            {
                case DefinitionOfDoneEvaluationOutcomeEnum.Passed:
                    if (stored.SkippedReason != null) return "carries a skipped reason";
                    if (hasFailureDetail) return "carries failure details";
                    return null;
                case DefinitionOfDoneEvaluationOutcomeEnum.Skipped:
                case DefinitionOfDoneEvaluationOutcomeEnum.NotVerifiable:
                    if (String.IsNullOrEmpty(stored.SkippedReason)) return "has no skipped reason";
                    if (hasFailureDetail) return "carries failure details";
                    return null;
                case DefinitionOfDoneEvaluationOutcomeEnum.Failed:
                case DefinitionOfDoneEvaluationOutcomeEnum.EvaluationError:
                    if (String.IsNullOrEmpty(stored.CommandLabel)) return "has no command label";
                    if (stored.SkippedReason != null) return "carries a skipped reason";
                    return null;
                default:
                    return "has an outcome with no field rule";
            }
        }

        /// <summary>
        /// Accept only an exact declared enum name. Numbers, combined flag names and case variants are rejected.
        /// </summary>
        private static bool TryParseExactName<T>(string? value, out T parsed) where T : struct, Enum
        {
            parsed = default;
            if (String.IsNullOrEmpty(value)) return false;

            foreach (string name in Enum.GetNames(typeof(T)))
            {
                if (String.Equals(name, value, StringComparison.Ordinal))
                {
                    parsed = Enum.Parse<T>(name);
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Private-Classes

        /// <summary>
        /// Stored payload shape with every field nullable and enums as text, so absence and unknown values are detectable.
        /// </summary>
        private sealed class StoredEvaluationPayload
        {
            public int? SchemaVersion { get; set; }
            public string? Outcome { get; set; }
            public string? SkippedReason { get; set; }
            public string? CommandLabel { get; set; }
            public int? ExitCode { get; set; }
            public string? FailureClass { get; set; }
            public string? OutputTail { get; set; }
            public string? CaptainId { get; set; }
            public string? DockId { get; set; }
            public string? BranchName { get; set; }
            public string? CommitHash { get; set; }
            public int? RecoveryAttempts { get; set; }
            public DateTime? StartedUtc { get; set; }
            public DateTime? CompletedUtc { get; set; }
        }

        private static void SetUnavailable(MissionDefinitionOfDoneReport report, string reason)
        {
            report.HistoryState = RecordedHistoryStateEnum.Unavailable;
            report.HistoryUnavailableReason = reason;
            report.LatestEvaluation = null;
        }

        #endregion
    }
}
