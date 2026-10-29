namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Finds the proof that a mission's work is already on its vessel's remote default branch: the mission's own
    /// commit is an ancestor of <c>origin/&lt;default branch&gt;</c>, or a commit on that ref carries the mission's
    /// exact <c>Armada-Mission-Id</c> trailer. A LandingFailed mission whose failure is a landing refusal, or whose
    /// recorded commit is the commit its stage started from, never has proof: the first holds work Armada declined to
    /// land, and the second names a commit that is on the default branch before the mission did any work.
    /// <see cref="ProveDeliveredAsync"/> reads the same proof for a mission in any status.
    /// </summary>
    /// <remarks>
    /// Git runs through the injected <see cref="IGitService"/>, so a vessel whose repository path is a Harbor checkout
    /// address is read on that Harbor. The repository must be fetched before the proof is read.
    /// </remarks>
    public sealed class LandingReconcileProof
    {
        #region Public-Members

        /// <summary>
        /// The commit trailer key that names the mission a commit belongs to.
        /// </summary>
        public const string MissionIdTrailerKey = "Armada-Mission-Id";

        #endregion

        #region Private-Members

        private readonly string _Header = "[LandingReconcileProof] ";
        private readonly DatabaseDriver _Database;
        private readonly IGitService _Git;
        private readonly LoggingModule _Logging;
        private readonly string? _LogDirectory;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="git">Git service for the vessel's repository.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="logDirectory">Server log directory that holds the stage start records, or null when none is kept.</param>
        public LandingReconcileProof(DatabaseDriver database, IGitService git, LoggingModule logging, string? logDirectory = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _LogDirectory = logDirectory;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The remote-tracking ref a vessel's work must reach.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <returns>The ref, for example <c>refs/remotes/origin/main</c>.</returns>
        public static string TargetRefOf(Vessel vessel)
        {
            return TargetRefOf(vessel, null);
        }

        /// <summary>
        /// The remote-tracking ref a vessel's work must reach on a named branch.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <param name="targetBranch">Branch the work was meant for, or null for the vessel's default branch.</param>
        /// <returns>The ref, for example <c>refs/remotes/origin/main</c>.</returns>
        public static string TargetRefOf(Vessel vessel, string? targetBranch)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            string branch = !String.IsNullOrWhiteSpace(targetBranch) ? targetBranch.Trim()
                : String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch.Trim();
            return "refs/remotes/origin/" + branch;
        }

        /// <summary>
        /// Read the proof for a LandingFailed mission.
        /// </summary>
        /// <param name="mission">Mission in LandingFailed.</param>
        /// <param name="vessel">The mission's vessel; its LocalPath is the repository to read.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The proof, or null when the mission is not eligible or the repository holds none.</returns>
        public async Task<LandingReconcileEvidence?> ProveAsync(Mission mission, Vessel vessel, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (mission.Status != MissionStatusEnum.LandingFailed) return null;
            if (LandingRefusalReasons.IsRefusal(mission.FailureReason)) return null;
            return await ProveDeliveredAsync(mission, vessel, null, null, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read the proof that a mission's work is on a branch of its vessel's remote, whatever the mission's status.
        /// </summary>
        /// <param name="mission">Mission whose commit or trailer is searched for.</param>
        /// <param name="vessel">The mission's vessel; its LocalPath is the repository to read.</param>
        /// <param name="startMission">
        /// Mission whose recorded stage start decides which commit is "before any work", or null for <paramref name="mission"/>
        /// itself. A later stage of a pipeline starts from its predecessor's tip, so it names the first stage here.
        /// </param>
        /// <param name="targetBranch">Branch the work was meant for, or null for the vessel's default branch.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The proof, or null when the repository holds none.</returns>
        public async Task<LandingReconcileEvidence?> ProveDeliveredAsync(
            Mission mission, Vessel vessel, Mission? startMission = null, string? targetBranch = null, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrWhiteSpace(vessel.LocalPath)) return null;

            string targetRef = TargetRefOf(vessel, targetBranch);
            string? commit = String.IsNullOrWhiteSpace(mission.CommitHash) ? null : mission.CommitHash.Trim();
            if (commit != null && await IsStageStartCommitAsync(startMission ?? mission, commit, token).ConfigureAwait(false)) commit = null;

            string? evidence = null;
            LandingEvidenceKindEnum kind = LandingEvidenceKindEnum.CommitAncestry;
            if (commit != null)
            {
                bool? contained = await _Git.TryIsAncestorAsync(vessel.LocalPath, commit, targetRef, token).ConfigureAwait(false);
                if (contained == true) evidence = commit;
            }

            if (evidence == null)
            {
                // Bounded by the mission's creation, with a day of slack for clock skew: its trailer cannot predate it.
                IReadOnlyList<string> trailered = await _Git.FindCommitsByTrailerAsync(
                    vessel.LocalPath, targetRef, MissionIdTrailerKey, mission.Id, mission.CreatedUtc.AddDays(-1), 1, token).ConfigureAwait(false);
                if (trailered.Count > 0 && !String.IsNullOrWhiteSpace(trailered[0]))
                {
                    evidence = trailered[0].Trim();
                    kind = LandingEvidenceKindEnum.MissionTrailer;
                }
            }

            if (evidence == null) return null;
            return new LandingReconcileEvidence
            {
                MissionId = mission.Id,
                VesselId = mission.VesselId,
                EvidenceCommit = evidence,
                EvidenceKind = kind,
                TargetRef = targetRef.StartsWith("refs/remotes/", StringComparison.Ordinal) ? targetRef.Substring("refs/remotes/".Length) : targetRef,
                FailureReason = mission.FailureReason,
                ProposedUtc = DateTime.UtcNow
            };
        }

        #endregion

        #region Private-Methods

        private async Task<bool> IsStageStartCommitAsync(Mission mission, string commit, CancellationToken token)
        {
            try
            {
                MissionStageStartCommit? start = await MissionStageStartCommitResolver.ResolveAsync(_Database, _LogDirectory, mission, _Logging, token).ConfigureAwait(false);
                return start != null && String.Equals(start.Commit.Trim(), commit, StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Without the start commit the ancestry cannot be told apart from an unchanged checkout, so the commit
                // is not used as proof; the trailer search does not depend on it.
                _Logging.Warn(_Header + "could not read the stage start commit of mission " + mission.Id + ": " + ex.Message);
                return true;
            }
        }

        #endregion
    }
}
