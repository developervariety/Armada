namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Reports Armada mission branches that exist in a vessel's repository but were never merged
    /// into its default branch.
    /// </summary>
    /// <remarks>
    /// Landing failures used to strand mission branches silently: the work was done, the branch was
    /// left behind, and the only way to notice was to go looking. This turns that into a number that
    /// can be checked on a schedule.
    /// </remarks>
    public sealed class UnlandedBranchService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly IBranchInventory _Git;
        private readonly LoggingModule? _Logging;
        private const string _Header = "[UnlandedBranchService] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver used to resolve vessels and missions.</param>
        /// <param name="git">Branch inventory used to enumerate and compare branches.</param>
        /// <param name="logging">Optional logging module.</param>
        public UnlandedBranchService(DatabaseDriver database, IBranchInventory git, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the unlanded-branch report for one vessel or for every vessel.
        /// </summary>
        /// <param name="vesselId">Vessel to measure, or null for all vessels.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One report per measured vessel.</returns>
        public async Task<List<UnlandedBranchReport>> BuildReportAsync(string? vesselId = null, CancellationToken token = default)
        {
            List<Vessel> vessels = new List<Vessel>();
            if (!String.IsNullOrWhiteSpace(vesselId))
            {
                Vessel? single = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
                if (single == null) throw new InvalidOperationException("Vessel not found: " + vesselId);
                vessels.Add(single);
            }
            else
            {
                vessels.AddRange(await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false));
            }

            List<UnlandedBranchReport> reports = new List<UnlandedBranchReport>();
            foreach (Vessel vessel in vessels)
            {
                reports.Add(await BuildForVesselAsync(vessel, token).ConfigureAwait(false));
            }

            return reports;
        }

        #endregion

        #region Private-Methods

        private async Task<UnlandedBranchReport> BuildForVesselAsync(Vessel vessel, CancellationToken token)
        {
            UnlandedBranchReport report = new UnlandedBranchReport
            {
                VesselId = vessel.Id,
                VesselName = vessel.Name,
                DefaultBranch = vessel.DefaultBranch
            };

            if (String.IsNullOrWhiteSpace(vessel.LocalPath))
            {
                report.Error = "Vessel has no LocalPath; cannot measure branches.";
                return report;
            }

            IReadOnlyList<string> branches = await _Git.EnumerateLocalBranchesAsync(
                vessel.LocalPath, Constants.BranchPrefix, token).ConfigureAwait(false);
            report.MissionBranchCount = branches.Count;

            foreach (string branch in branches)
            {
                bool merged = await _Git.IsAncestorAsync(vessel.LocalPath, branch, vessel.DefaultBranch, token).ConfigureAwait(false);
                if (merged) continue;

                UnlandedBranchEntry entry = new UnlandedBranchEntry
                {
                    BranchName = branch,
                    MissionId = ExtractMissionId(branch)
                };

                if (!String.IsNullOrEmpty(entry.MissionId))
                {
                    Mission? mission = await _Database.Missions.ReadAsync(entry.MissionId, token).ConfigureAwait(false);
                    entry.MissionStatus = mission?.Status.ToString();
                }

                report.Unlanded.Add(entry);
            }

            report.UnlandedCount = report.Unlanded.Count;
            if (report.UnlandedCount > 0)
            {
                _Logging?.Warn(_Header + "vessel " + vessel.Name + " has " + report.UnlandedCount +
                    " unlanded mission branch(es) against " + vessel.DefaultBranch);
            }

            return report;
        }

        /// <summary>
        /// Mission branches are named "armada/&lt;captain&gt;/&lt;missionId&gt;". Anything else, such as a
        /// merge-queue integration branch, yields null rather than a bogus id.
        /// </summary>
        private static string? ExtractMissionId(string branchName)
        {
            if (String.IsNullOrWhiteSpace(branchName)) return null;

            int lastSlash = branchName.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash == branchName.Length - 1) return null;

            string candidate = branchName.Substring(lastSlash + 1);
            if (!candidate.StartsWith(Constants.MissionIdPrefix, StringComparison.Ordinal)) return null;
            return candidate;
        }

        #endregion
    }
}
