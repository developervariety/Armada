namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Harbor;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp;

    /// <summary>
    /// The one handler for a mission change summary. REST and MCP call it, so both surfaces apply the same
    /// authorization, bounds and answer shape.
    ///
    /// The mission is resolved from the caller: a mission credential always reads its own mission, whatever id is
    /// supplied, and never inherits its owner's operator rights; any other caller reads a mission its tenant and
    /// role policy lets it read. The vessel and repository come from that mission, never from the request, and the
    /// two commits come from recorded mission evidence, never from the request, so no supplied path, URL or commit
    /// grants access. Both commits are full object ids, so moving a branch never changes what a response compares.
    /// It reads the vessel's repository only: it needs no dock, writes nothing, and calls no embedding or
    /// classifier provider.
    /// </summary>
    public static class MissionChangeSummaryReader
    {
        #region Private-Members

        private static readonly string _Header = "[MissionChangeSummaryReader] ";

        private const string _ScopeWarning =
            "Paths compare the stage's start commit with its recorded final commit. This is not a diff against the current target branch and holds no uncommitted files.";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the change summary of a mission.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logDirectory">Server log directory holding dock start records.</param>
        /// <param name="git">Git service; null when none is configured.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="suppliedMissionId">Mission id the request named, or null.</param>
        /// <param name="maxPaths">Requested path limit, or null for the default.</param>
        /// <param name="bounds">Bounds to apply; null uses <see cref="MissionChangeSummaryBounds.Default"/>.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="token">Caller cancellation token. Cancellation propagates.</param>
        /// <returns>The summary, or the named reason there is none.</returns>
        public static async Task<MissionChangeSummary> ReadAsync(
            DatabaseDriver database,
            string? logDirectory,
            IGitService? git,
            AuthContext caller,
            string? suppliedMissionId,
            int? maxPaths,
            MissionChangeSummaryBounds? bounds = null,
            LoggingModule? logging = null,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            MissionChangeSummaryBounds limits = bounds ?? MissionChangeSummaryBounds.Default;
            token.ThrowIfCancellationRequested();

            MissionChangeSummary summary = new MissionChangeSummary();
            summary.MaxPaths = limits.ResolveMaxPaths(maxPaths);
            summary.Warnings.Add(_ScopeWarning);

            // A mission credential names the mission it was issued to, and that mission wins over a supplied id.
            string? supplied = String.IsNullOrWhiteSpace(suppliedMissionId) ? null : suppliedMissionId!.Trim();
            string? own = String.IsNullOrWhiteSpace(caller.MissionId) ? null : caller.MissionId!.Trim();
            string? missionId = own ?? supplied;
            if (own != null && supplied != null && !String.Equals(own, supplied, StringComparison.Ordinal))
                summary.Warnings.Add("The supplied mission id names another mission, so it was ignored: a mission credential reads only its own mission.");
            if (missionId == null)
                return Refuse(summary, MissionChangeSummary.InvalidReason, "Name the mission to summarize.");
            summary.MissionId = missionId;

            try
            {
                Mission? mission = await McpMissionEvidenceScope.ReadMissionAsync(database, caller, missionId).ConfigureAwait(false);
                if (mission == null)
                    return Refuse(summary, MissionChangeSummary.MissionNotFoundReason, "No mission has that id.");
                summary.MissionId = mission.Id;

                if (own != null)
                {
                    summary.CallsUsed = limits.ChargeCall(own);
                    if (summary.CallsUsed > limits.MaxCallsPerMission)
                        return Refuse(summary, MissionChangeSummary.BudgetReason,
                            "This mission has used its change-summary budget of " + limits.MaxCallsPerMission + " calls.");
                }

                summary.FinalCommit = MissionStageStartCommitResolver.IsFullCommitId(mission.CommitHash) ? mission.CommitHash!.Trim() : null;
                if (String.IsNullOrWhiteSpace(mission.VesselId))
                    return Refuse(summary, MissionChangeSummary.NoVesselReason, "That mission has no vessel.");
                summary.VesselId = mission.VesselId;

                // The vessel is the mission's own, in the mission's tenant; its repository path is never supplied.
                Vessel? vessel = await database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
                if (vessel == null || !OwnershipPolicy.SameTenant(vessel.TenantId, mission.TenantId))
                    return Refuse(summary, MissionChangeSummary.RepositoryUnavailableReason, "The mission's vessel repository is not readable on this admiral.");

                Dock? dock = null;
                if (!String.IsNullOrWhiteSpace(mission.DockId))
                {
                    dock = await CallerScopedRead.ReadDockAsync(database, caller, mission.DockId, token).ConfigureAwait(false);
                    if (dock == null)
                        return Refuse(summary, MissionChangeSummary.RepositoryUnavailableReason, "The mission's recorded dock is not readable.");
                }
                string repositoryPath;
                if (dock?.HostCheckout != null)
                {
                    if (!dock.HostCheckout.IsComplete || !String.Equals(dock.VesselId, mission.VesselId, StringComparison.Ordinal))
                        return Refuse(summary, MissionChangeSummary.RepositoryUnavailableReason, "The mission's Harbor checkout binding is incomplete.");
                    repositoryPath = HarborCheckoutAddress.ForDock(dock.Id) + "/repository";
                }
                else
                {
                    if (String.IsNullOrWhiteSpace(vessel.LocalPath) || !Directory.Exists(vessel.LocalPath))
                        return Refuse(summary, MissionChangeSummary.RepositoryUnavailableReason, "The mission's vessel repository is not readable on this admiral.");
                    repositoryPath = vessel.LocalPath;
                }

                MissionStageStartCommit? start = await MissionStageStartCommitResolver.ResolveAsync(
                    database, logDirectory, mission, logging, token).ConfigureAwait(false);
                if (start != null)
                {
                    summary.StartCommit = start.Commit;
                    summary.StartCommitSource = start.Source;
                }
                if (start == null)
                    return Refuse(summary, MissionChangeSummary.StartCommitNotRecordedReason,
                        "No recorded evidence names the commit this stage started from, so its own change cannot be separated from what it inherited.");
                if (summary.FinalCommit == null)
                    return Refuse(summary, MissionChangeSummary.FinalCommitNotRecordedReason,
                        "The mission has no recorded final commit; it may not have produced work yet.");
                if (git == null)
                    return Refuse(summary, MissionChangeSummary.GitUnavailableReason, "No git service is configured on this admiral.");

                if (!await limits.TryTakeSlotAsync(token).ConfigureAwait(false))
                    return Refuse(summary, MissionChangeSummary.BusyReason, "Too many change summaries are being read; retry shortly.");
                try
                {
                    return await CompareAsync(summary, git, repositoryPath, limits, token).ConfigureAwait(false);
                }
                finally
                {
                    limits.ReleaseSlot();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logging?.Warn(_Header + "change summary for mission " + missionId + " failed: " + ex.Message);
                return Refuse(summary, MissionChangeSummary.ExceptionReason, "The change summary could not be read.");
            }
        }

        #endregion

        #region Private-Methods

        private static async Task<MissionChangeSummary> CompareAsync(
            MissionChangeSummary summary,
            IGitService git,
            string repoPath,
            MissionChangeSummaryBounds limits,
            CancellationToken token)
        {
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(limits.Deadline);
                ChangedPathStatusRead read;
                try
                {
                    read = await git.ReadChangedPathStatusesBetweenCommitsAsync(
                        repoPath, summary.StartCommit!, summary.FinalCommit!, summary.MaxPaths, limits.Deadline,
                        limits.OutputLimitBytes, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    return Refuse(summary, ChangedPathStatusRead.TimeoutCode, "The change summary passed its deadline and was stopped.");
                }

                if (!read.Available)
                    return Refuse(summary, read.FailureCode ?? ChangedPathStatusRead.GitFailedCode, read.Message ?? "The comparison could not be read.");

                summary.Available = true;
                summary.Complete = read.Complete;
                summary.Reason = read.Complete ? null : read.IncompleteReason;
                summary.Message = read.Complete ? null : read.Message;
                summary.Paths = read.Entries.ToList();
                summary.PathCount = summary.Paths.Count;
                summary.TotalPathCount = read.TotalCount;
                return summary;
            }
        }

        private static MissionChangeSummary Refuse(MissionChangeSummary summary, string reason, string message)
        {
            summary.Available = false;
            summary.Complete = false;
            summary.Reason = reason;
            summary.Message = message;
            summary.Paths = new List<ChangedPathEntry>();
            summary.PathCount = 0;
            summary.TotalPathCount = null;
            return summary;
        }

        #endregion
    }
}
