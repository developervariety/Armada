namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Json;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Replaces a green Check that measured older work with a fresh Pending record for the work a
    /// live voyage is now reviewing.
    /// </summary>
    /// <remarks>
    /// A voyage-armed Check is stamped once, at the first stage that commits, and never re-stamped.
    /// Every stage after that commits on top, so by the time the Judge runs the only green record
    /// can vouch for a commit several stages back. The Judge gate holds a PASS when it sees such a
    /// record (<see cref="CheckRunGateRules.IsStale"/>); this service is what lets the hold end,
    /// by giving the executor a record it can run against the current tip.
    /// <para>
    /// The stale record is Canceled, not deleted or rewritten: it stays as the honest history of
    /// what it measured, its summary names the record that supersedes it, and a Canceled record
    /// is ignored by every gate. That is the same rule an operator follows by hand when a flake is
    /// retried to green.
    /// </para>
    /// </remarks>
    public class StaleCheckSupersessionService
    {
        #region Private-Members

        private readonly string _Header = "[StaleCheckSupersession] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule? _Logging;
        private readonly JsonSerializerOptions _JsonOptions = JsonDefaults.Web;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module, optional.</param>
        public StaleCheckSupersessionService(DatabaseDriver database, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The mission whose commit a voyage-armed Check should measure: the most recently updated
        /// stage that has committed to a branch. Null while no stage has produced measurable work.
        /// </summary>
        /// <remarks>
        /// This is the one definition of "the work under review" for voyage-armed Checks. The
        /// executor stamps from it and this service compares against it, so the two cannot disagree
        /// about which commit a record ought to carry.
        /// </remarks>
        /// <param name="missions">The voyage's missions, in any order.</param>
        /// <returns>The mission carrying the work under review, or null.</returns>
        public static Mission? SelectWorkUnderReview(IEnumerable<Mission>? missions)
        {
            if (missions == null) return null;
            return missions
                .Where(mission => mission != null)
                .Where(mission => !String.IsNullOrWhiteSpace(mission.BranchName))
                .Where(mission => !String.IsNullOrWhiteSpace(mission.CommitHash))
                .Where(mission => mission.Status == MissionStatusEnum.WorkProduced
                    || mission.Status == MissionStatusEnum.PullRequestOpen
                    || mission.Status == MissionStatusEnum.Testing
                    || mission.Status == MissionStatusEnum.Review
                    || mission.Status == MissionStatusEnum.Complete)
                .OrderByDescending(mission => mission.LastUpdateUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Decide whether a fresh record is needed once a stale green has been ruled out. None is
        /// needed when a sibling of the same type already covers the work: one still queued or
        /// running (it is stamped at execution and will measure the current work), or one that
        /// Passed at the current commit.
        /// </summary>
        /// <param name="stale">The record being superseded.</param>
        /// <param name="siblings">Every other record attached to the same voyage.</param>
        /// <param name="workCommit">The commit under review.</param>
        /// <returns>True when a replacement record must be created.</returns>
        public static bool NeedsReplacement(CheckRun stale, IEnumerable<CheckRun>? siblings, string? workCommit)
        {
            if (stale == null) throw new ArgumentNullException(nameof(stale));
            if (siblings == null) return true;

            foreach (CheckRun sibling in siblings)
            {
                if (sibling == null || String.Equals(sibling.Id, stale.Id, StringComparison.Ordinal)) continue;
                if (sibling.Type != stale.Type) continue;
                if (sibling.Status == CheckRunStatusEnum.Pending || sibling.Status == CheckRunStatusEnum.Running) return false;
                if (sibling.Status == CheckRunStatusEnum.Passed && CheckRunGateRules.SameCommit(sibling.CommitHash, workCommit)) return false;
            }

            return true;
        }

        /// <summary>
        /// Sweep every live voyage for green Checks that measured older work and supersede each one.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of records superseded.</returns>
        public async Task<int> SupersedeAsync(CancellationToken token = default)
        {
            int superseded = 0;
            List<Voyage> live = await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.InProgress, token).ConfigureAwait(false);

            foreach (Voyage voyage in live)
            {
                if (voyage == null) continue;
                superseded += await SupersedeForVoyageAsync(voyage, token).ConfigureAwait(false);
            }

            return superseded;
        }

        /// <summary>
        /// Supersede the stale greens attached to one voyage.
        /// </summary>
        /// <param name="voyage">A voyage that is still in progress.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of records superseded.</returns>
        public async Task<int> SupersedeForVoyageAsync(Voyage voyage, CancellationToken token = default)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            if (voyage.Status != VoyageStatusEnum.InProgress) return 0;

            List<Mission> missions = await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            Mission? work = SelectWorkUnderReview(missions);
            if (work == null) return 0;

            EnumerationResult<CheckRun> attached = await _Database.CheckRuns
                .EnumerateAsync(new CheckRunQuery { VoyageId = voyage.Id, PageNumber = 1, PageSize = 200 }, token)
                .ConfigureAwait(false);
            List<CheckRun> records = attached.Objects.Where(run => run != null).ToList();

            int superseded = 0;
            foreach (CheckRun stale in records.Where(run => CheckRunGateRules.IsStale(run, work.CommitHash)).ToList())
            {
                CheckRun? replacement = null;
                if (NeedsReplacement(stale, records, work.CommitHash))
                {
                    replacement = new CheckRun
                    {
                        TenantId = stale.TenantId,
                        UserId = stale.UserId,
                        VesselId = stale.VesselId,
                        VoyageId = stale.VoyageId,
                        WorkflowProfileId = stale.WorkflowProfileId,
                        Type = stale.Type,
                        Source = stale.Source,
                        Status = CheckRunStatusEnum.Pending,
                        Label = stale.Type.ToString() + " (re-armed: work moved to " + Abbreviate(work.CommitHash) + ")"
                    };
                    replacement = await _Database.CheckRuns.CreateAsync(replacement, token).ConfigureAwait(false);
                    records.Add(replacement);
                }

                stale.Status = CheckRunStatusEnum.Canceled;
                stale.Summary = "Superseded"
                    + (replacement == null ? String.Empty : " by " + replacement.Id)
                    + ": this record passed at " + Abbreviate(stale.CommitHash)
                    + " but the work under review moved to " + Abbreviate(work.CommitHash)
                    + " (mission " + work.Id + "). A green for older work does not vouch for newer work.";
                stale.LastUpdateUtc = DateTime.UtcNow;
                await _Database.CheckRuns.UpdateAsync(stale, token).ConfigureAwait(false);

                await WriteEventAsync(stale, replacement, work, token).ConfigureAwait(false);
                _Logging?.Info(_Header + "superseded check " + stale.Id + " (passed at " + Abbreviate(stale.CommitHash)
                    + ") on voyage " + voyage.Id + "; work under review is " + Abbreviate(work.CommitHash)
                    + (replacement == null ? "; a sibling already covers it" : "; re-armed as " + replacement.Id));
                superseded++;
            }

            return superseded;
        }

        #endregion

        #region Private-Methods

        private static string Abbreviate(string? commit)
        {
            if (String.IsNullOrWhiteSpace(commit)) return "(none)";
            string trimmed = commit.Trim();
            return trimmed.Length <= 12 ? trimmed : trimmed.Substring(0, 12);
        }

        private async Task WriteEventAsync(CheckRun stale, CheckRun? replacement, Mission work, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(
                "check.superseded",
                "Check " + stale.Id + " superseded: passed at " + Abbreviate(stale.CommitHash)
                    + ", work under review moved to " + Abbreviate(work.CommitHash))
            {
                TenantId = stale.TenantId,
                UserId = stale.UserId,
                EntityType = "check_run",
                EntityId = stale.Id,
                MissionId = work.Id,
                VesselId = stale.VesselId,
                VoyageId = stale.VoyageId,
                Payload = JsonSerializer.Serialize(new
                {
                    StaleCheckId = stale.Id,
                    ReplacementCheckId = replacement?.Id,
                    stale.Type,
                    MeasuredCommit = stale.CommitHash,
                    WorkCommit = work.CommitHash,
                    WorkMissionId = work.Id
                }, _JsonOptions)
            };

            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        #endregion
    }
}
