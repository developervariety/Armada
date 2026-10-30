namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Real battery dispatch: one evaluation voyage on the fixture vessel with the Worker persona, requesting the
    /// captain under test. The fixture vessel's landing mode is None. Dispatch waits until completion handling
    /// records the matching gate and final outcome and releases the captain, then grades an isolated checkout of
    /// the recorded commit. A timeout cancels the voyage.
    /// </summary>
    public class BatteryTaskDispatcher : IBatteryTaskDispatcher
    {
        #region Public-Members

        /// <summary>
        /// Mission statuses after which a battery task is graded.
        /// </summary>
        public static readonly IReadOnlyList<MissionStatusEnum> FinishedStatuses = new List<MissionStatusEnum>
        {
            MissionStatusEnum.WorkProduced, MissionStatusEnum.Review, MissionStatusEnum.Complete, MissionStatusEnum.Failed,
            MissionStatusEnum.LandingFailed, MissionStatusEnum.Cancelled
        };

        #endregion

        #region Private-Members

        private readonly string _Header = "[BatteryTaskDispatcher] ";
        private readonly IAdmiralService _Admiral;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly Func<Voyage, CancellationToken, Task> _CancelVoyage;
        private readonly TimeSpan _PollInterval;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="admiral">Admiral service (dispatch).</param>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="cancelVoyage">Cancels a voyage and its open missions.</param>
        /// <param name="pollInterval">How often to read the mission, or null for 5 seconds.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        public BatteryTaskDispatcher(IAdmiralService admiral, DatabaseDriver database, LoggingModule logging, Func<Voyage, CancellationToken, Task> cancelVoyage, TimeSpan? pollInterval = null)
        {
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _CancelVoyage = cancelVoyage ?? throw new ArgumentNullException(nameof(cancelVoyage));
            _PollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<BatteryDispatchResult> RunAsync(BatteryDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            BatteryDispatchResult result = new BatteryDispatchResult();

            List<CaptainAssignmentOverride> overrides = new List<CaptainAssignmentOverride>
            {
                new CaptainAssignmentOverride(PersonaCatalog.Worker, request.Captain.Id, null)
            };

            Voyage voyage;
            try
            {
                voyage = await _Admiral.DispatchVoyageQueuedAsync(
                    "Battery: " + request.Title,
                    request.Description,
                    request.Vessel.Id,
                    new List<MissionDescription> { new MissionDescription(request.Title, request.Description) },
                    BatteryFixtureService.PipelineName,
                    null,
                    null,
                    overrides,
                    token,
                    true).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                result.FailureReason = "Dispatch failed: " + ex.Message;
                return result;
            }

            result.VoyageId = voyage.Id;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, request.TimeoutMs));
            Mission? current = null;
            while (true)
            {
                List<Mission> missions = await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
                current = missions.OrderBy(m => m.CreatedUtc).LastOrDefault();
                if (current != null && missions.All(m => FinishedStatuses.Contains(m.Status)))
                {
                    bool completing = false;
                    foreach (Mission mission in missions)
                    {
                        if ((mission.Status == MissionStatusEnum.WorkProduced || mission.Status == MissionStatusEnum.Review
                            || mission.Status == MissionStatusEnum.Complete)
                            && !await HasCompletionEvidenceAsync(mission, token).ConfigureAwait(false)) completing = true;
                        if (String.IsNullOrEmpty(mission.CaptainId)) continue;
                        Captain? assigned = await _Database.Captains.ReadAsync(mission.CaptainId!, token).ConfigureAwait(false);
                        if (assigned?.CurrentMissionId == mission.Id) completing = true;
                    }
                    if (!completing)
                    {
                        current = await _Database.Missions.ReadAsync(current.Id, token).ConfigureAwait(false) ?? current;
                        break;
                    }
                }
                if (DateTime.UtcNow >= deadline)
                {
                    result.TimedOut = true;
                    try
                    {
                        await _CancelVoyage(voyage, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        _Logging.Warn(_Header + "could not cancel timed-out voyage " + voyage.Id + ": " + ex.Message);
                    }

                    if (current != null) current = await _Database.Missions.ReadAsync(current.Id, CancellationToken.None).ConfigureAwait(false) ?? current;
                    break;
                }

                await Task.Delay(_PollInterval, token).ConfigureAwait(false);
            }

            result.Mission = current;
            if (current != null && !String.IsNullOrEmpty(current.DockId))
            {
                Dock? dock = await _Database.Docks.ReadAsync(current.DockId!, token).ConfigureAwait(false);
                if (dock == null || String.IsNullOrWhiteSpace(dock.WorktreePath))
                    result.WorkspaceUnavailableReason = "The mission's dock has no worktree path.";
                else if (dock.HostCheckout != null)
                    result.WorkspaceUnavailableReason = "The dock lives on a Harbor runner; battery grading runs only on worktrees on the Admiral host.";
                else
                {
                    if (String.IsNullOrWhiteSpace(current.CommitHash) || String.IsNullOrWhiteSpace(request.Vessel.LocalPath))
                        result.WorkspaceUnavailableReason = "The completed mission has no recorded commit or local repository to grade.";
                    else if (!result.TimedOut && current.Status != MissionStatusEnum.Failed
                        && current.Status != MissionStatusEnum.Cancelled && current.Status != MissionStatusEnum.LandingFailed)
                        result.Workspace = await LocalBatteryWorkspace.CreateSnapshotAsync(request.Vessel.LocalPath!, current.CommitHash!, token).ConfigureAwait(false);
                }
            }

            return result;
        }

        #endregion

        #region Private-Methods

        private async Task<bool> HasCompletionEvidenceAsync(Mission mission, CancellationToken token)
        {
            EnumerationResult<ArmadaEvent> gates = await _Database.Events.EnumerateAsync(new EnumerationQuery
            { MissionId = mission.Id, EventType = DefinitionOfDoneEvaluationRecord.EventType, PageSize = 1 }, token).ConfigureAwait(false);
            ArmadaEvent? latest = gates.Objects.FirstOrDefault(item => SameOwner(item, mission));
            if (latest == null || String.IsNullOrWhiteSpace(latest.Payload)) return false;
            DefinitionOfDoneEvaluationRecord? gate;
            try
            {
                gate = JsonSerializer.Deserialize<DefinitionOfDoneEvaluationRecord>(latest.Payload);
            }
            catch (JsonException ex)
            {
                _Logging.Warn(_Header + "cannot verify completion gate for " + mission.Id + ": " + ex.Message);
                return false;
            }
            if (gate == null || gate.Outcome != DefinitionOfDoneEvaluationOutcomeEnum.Passed
                || gate.StartedUtc < (mission.StartedUtc ?? mission.CreatedUtc)
                || gate.RecoveryAttempts != mission.RecoveryAttempts || gate.CaptainId != mission.CaptainId
                || gate.DockId != mission.DockId || String.IsNullOrWhiteSpace(mission.CommitHash)
                || !String.Equals(gate.CommitHash, mission.CommitHash, StringComparison.OrdinalIgnoreCase)) return false;
            EnumerationResult<ArmadaEvent> outcomes = await _Database.Events.EnumerateAsync(new EnumerationQuery
            { MissionId = mission.Id, EventType = "mission.work_produced", PageSize = 1 }, token).ConfigureAwait(false);
            return outcomes.Objects.Any(item => SameOwner(item, mission) && item.CreatedUtc >= gate.CompletedUtc);
        }

        private static bool SameOwner(ArmadaEvent item, Mission mission)
        {
            return (item.TenantId ?? Constants.DefaultTenantId) == (mission.TenantId ?? Constants.DefaultTenantId)
                && (item.UserId ?? Constants.DefaultUserId) == (mission.UserId ?? Constants.DefaultUserId);
        }

        #endregion
    }
}
