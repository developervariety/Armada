namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Records an operator's delivery proof on a Failed merge entry the landing sweep cannot prove: an entry with no
    /// mission, or an entry whose mission was Cancelled and whose branch is gone. An operator names the commit on the
    /// target branch and this recorder proves it before it stores anything. REST
    /// <c>POST /api/v1/merge-queue/{id}/delivery-evidence</c> and MCP <c>armada_record_merge_delivery</c> call it.
    /// </summary>
    /// <remarks>
    /// The named commit must be on <c>origin/&lt;target branch&gt;</c>, read after a fetch. The work it is compared with
    /// always comes from the record: the mission's commit, or for an entry with no mission the branch tip the vessel
    /// repository resolves. An ancestry proof needs the commit to be that work; a patch-id proof needs the commit's
    /// <c>git patch-id --stable</c> to equal the work's, which proves a cherry-pick or a rebase of the same change. An
    /// entry with no mission whose branch is gone has no work to compare, so only an ancestry proof records it.
    /// The recorder name comes from the authenticated caller through <see cref="CallerIdentity"/>, never from the
    /// request. The entry keeps its Failed outcome, the mission is never changed, and nothing is deleted. Each refusal
    /// has its own code and leaves the entry unchanged. Only a global administrator reaches the entry; any other caller
    /// is answered as not found.
    /// </remarks>
    public sealed class MergeDeliveryRecorder
    {
        #region Public-Members

        /// <summary>Refusal: the entry does not exist in the caller's scope.</summary>
        public const string NotFoundCode = "merge_entry_not_found";

        /// <summary>Refusal: the entry is not Failed.</summary>
        public const string NotFailedCode = "merge_entry_not_failed";

        /// <summary>Refusal: the entry already carries delivery proof.</summary>
        public const string AlreadyRecordedCode = "merge_delivery_already_recorded";

        /// <summary>Refusal: the entry belongs to a mission that is not Cancelled or whose branch still resolves, so the landing sweep reads its proof.</summary>
        public const string MissionBoundCode = "merge_entry_mission_bound";

        /// <summary>Refusal: the entry has no vessel, the vessel has no repository, or the repository cannot be fetched or its branch read.</summary>
        public const string RepositoryUnavailableCode = "merge_repository_unavailable";

        /// <summary>Refusal: the caller carries no principal, credential or user name to record.</summary>
        public const string NoCallerIdentityCode = "merge_delivery_no_caller_identity";

        /// <summary>Refusal: no commit was given, or it is not proven to be on the target branch.</summary>
        public const string NotOnTargetCode = "merge_commit_not_on_target";

        /// <summary>Refusal: for an ancestry proof, the entry's work (its branch tip or its mission's commit) is a different commit.</summary>
        public const string BranchTipDiffersCode = "merge_branch_tip_differs";

        /// <summary>Refusal: the record names no work to compare with, or the work's patch cannot be read.</summary>
        public const string SourceCommitUnavailableCode = "merge_source_commit_unavailable";

        /// <summary>Refusal: the commit's patch-id is not the work's patch-id, or cannot be read.</summary>
        public const string PatchIdMismatchCode = "merge_patch_id_mismatch";

        /// <summary>Refusal: the requested proof kind is neither ancestry nor patch-id.</summary>
        public const string ProofKindUnsupportedCode = "merge_proof_kind_unsupported";

        /// <summary>Event type recorded once when an operator records delivery proof on an entry.</summary>
        public const string RecordedEventType = "merge.delivery_recorded";

        #endregion

        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly IGitService _Git;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="git">Git service; a Harbor-aware service reads a Harbor vessel on its Harbor.</param>
        public MergeDeliveryRecorder(DatabaseDriver database, IGitService git)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Prove a commit is the entry's delivered work and store the proof on the entry.
        /// </summary>
        /// <param name="entryId">Merge entry identifier.</param>
        /// <param name="commit">Commit the operator says carries the entry's work.</param>
        /// <param name="caller">Authenticated caller; its name is the recorder.</param>
        /// <param name="incidentId">Optional incident the record answers.</param>
        /// <param name="proofKind">The proof to read: <see cref="LandingEvidenceKindEnum.CommitAncestry"/> or <see cref="LandingEvidenceKindEnum.PatchId"/>.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored record or the refusal.</returns>
        public async Task<MergeDeliveryRecordResult> RecordAsync(string entryId, string commit, AuthContext? caller, string? incidentId = null,
            LandingEvidenceKindEnum proofKind = LandingEvidenceKindEnum.CommitAncestry, CancellationToken token = default)
        {
            MergeEntry? entry = String.IsNullOrWhiteSpace(entryId) || caller == null || !caller.IsAuthenticated || !caller.IsAdmin
                ? null
                : await _Database.MergeEntries.ReadAsync(entryId.Trim(), token).ConfigureAwait(false);
            if (entry == null)
                return MergeDeliveryRecordResult.Refused(null, NotFoundCode, "Merge entry not found");
            if (entry.Status != MergeStatusEnum.Failed)
                return MergeDeliveryRecordResult.Refused(entry, NotFailedCode,
                    "Merge entry " + entry.Id + " is " + entry.Status + "; delivery proof is recorded only on a Failed entry.");
            if (entry.DeliveryEvidence != null)
                return MergeDeliveryRecordResult.Refused(entry, AlreadyRecordedCode,
                    "Merge entry " + entry.Id + " already carries delivery proof: " + entry.DeliveryEvidence.Describe());
            if (proofKind != LandingEvidenceKindEnum.CommitAncestry && proofKind != LandingEvidenceKindEnum.PatchId)
                return MergeDeliveryRecordResult.Refused(entry, ProofKindUnsupportedCode,
                    "Proof kind " + proofKind + " is not recorded by an operator; use CommitAncestry or PatchId.");

            Mission? mission = null;
            if (!String.IsNullOrEmpty(entry.MissionId))
            {
                mission = await _Database.Missions.ReadAsync(entry.MissionId!, token).ConfigureAwait(false);
                if (mission == null || mission.Status != MissionStatusEnum.Cancelled)
                    return MergeDeliveryRecordResult.Refused(entry, MissionBoundCode,
                        "Merge entry " + entry.Id + " belongs to mission " + entry.MissionId
                        + (mission == null ? ", which is not found" : ", which is " + mission.Status)
                        + "; the landing sweep reads its proof. Only a Cancelled mission's entry is recorded by an operator.");
            }

            Vessel? vessel = String.IsNullOrEmpty(entry.VesselId) ? null : await _Database.Vessels.ReadAsync(entry.VesselId!, token).ConfigureAwait(false);
            if (vessel == null || String.IsNullOrWhiteSpace(vessel.LocalPath))
                return MergeDeliveryRecordResult.Refused(entry, RepositoryUnavailableCode,
                    "Merge entry " + entry.Id + " has no vessel repository to read the proof from.");

            string? recordedBy = CallerIdentity.Of(caller);
            if (recordedBy == null)
                return MergeDeliveryRecordResult.Refused(entry, NoCallerIdentityCode,
                    "The caller carries no principal, credential or user name, so the record would name nobody.");

            string wanted = (commit ?? String.Empty).Trim();
            if (wanted.Length == 0)
                return MergeDeliveryRecordResult.Refused(entry, NotOnTargetCode, "No commit was given.");

            try
            {
                await _Git.FetchAsync(vessel.LocalPath, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                return MergeDeliveryRecordResult.Refused(entry, RepositoryUnavailableCode,
                    "The repository of vessel " + vessel.Id + " cannot be fetched, so the proof is not read on stale refs: " + ex.Message);
            }

            // A branch counts as gone only when git says so; a read that fails is refused, never taken as absent.
            string? branchTip = null;
            if (!String.IsNullOrWhiteSpace(entry.BranchName))
            {
                string branch = entry.BranchName.Trim();
                bool? present = await _Git.TryBranchExistsAsync(vessel.LocalPath, branch, token).ConfigureAwait(false);
                if (present == null)
                    return MergeDeliveryRecordResult.Refused(entry, RepositoryUnavailableCode,
                        "Branch " + branch + " cannot be read in the repository of vessel " + vessel.Id + ", so whether it is gone is not known.");
                if (present == true)
                {
                    branchTip = (await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, "refs/heads/" + branch, token).ConfigureAwait(false))?.Trim();
                    if (String.IsNullOrEmpty(branchTip))
                        return MergeDeliveryRecordResult.Refused(entry, RepositoryUnavailableCode,
                            "Branch " + branch + " exists in the repository of vessel " + vessel.Id + ", but its tip cannot be read.");
                }
            }
            if (mission != null && branchTip != null)
                return MergeDeliveryRecordResult.Refused(entry, MissionBoundCode,
                    "Branch " + entry.BranchName + " of mission " + mission.Id + " still resolves at " + branchTip
                    + ", so its work can still change; the landing sweep reads its proof.");

            string targetRef = LandingReconcileProof.TargetRefOf(vessel, entry.TargetBranch);
            string target = targetRef.StartsWith("refs/remotes/", StringComparison.Ordinal) ? targetRef.Substring("refs/remotes/".Length) : targetRef;
            bool? contained = await _Git.TryIsAncestorAsync(vessel.LocalPath, wanted, targetRef, token).ConfigureAwait(false);
            if (contained != true)
                return MergeDeliveryRecordResult.Refused(entry, NotOnTargetCode,
                    "Commit " + wanted + (contained == false ? " is not on " : " cannot be proven on ") + target + ".");

            // The work always comes from the record, never from the request.
            string? source = mission != null
                ? (String.IsNullOrWhiteSpace(mission.CommitHash) ? null : mission.CommitHash.Trim())
                : branchTip;
            string sourceName = mission != null ? "mission " + mission.Id + "'s commit" : "branch " + entry.BranchName;
            if (source == null && (mission != null || proofKind == LandingEvidenceKindEnum.PatchId))
                return MergeDeliveryRecordResult.Refused(entry, SourceCommitUnavailableCode,
                    (mission != null ? "Mission " + mission.Id + " recorded no commit" : "Branch " + entry.BranchName + " does not resolve")
                    + ", so there is no work to compare commit " + wanted + " with.");

            if (proofKind == LandingEvidenceKindEnum.CommitAncestry)
            {
                if (source != null && !String.Equals(source, wanted, StringComparison.OrdinalIgnoreCase))
                    return MergeDeliveryRecordResult.Refused(entry, BranchTipDiffersCode,
                        (mission != null ? "Mission " + mission.Id + "'s commit is " : "Branch " + entry.BranchName + " is at ")
                        + source + ", not commit " + wanted + "; a patch-id proof compares a different commit.");
            }
            else
            {
                string? sourcePatch = await _Git.GetPatchIdAsync(vessel.LocalPath, source!, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(sourcePatch))
                    return MergeDeliveryRecordResult.Refused(entry, SourceCommitUnavailableCode,
                        "The patch of " + sourceName + " " + source + " cannot be read, so there is no work to compare commit " + wanted + " with.");
                string? wantedPatch = await _Git.GetPatchIdAsync(vessel.LocalPath, wanted, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(wantedPatch) || !String.Equals(wantedPatch.Trim(), sourcePatch.Trim(), StringComparison.OrdinalIgnoreCase))
                    return MergeDeliveryRecordResult.Refused(entry, PatchIdMismatchCode,
                        "Commit " + wanted + (String.IsNullOrWhiteSpace(wantedPatch) ? " has no readable patch" : " carries a different patch")
                        + " than " + sourceName + " " + source + ".");
            }

            // Write onto the current row, and only while the entry is still a failure with no proof.
            MergeEntry? current = await _Database.MergeEntries.ReadAsync(entry.Id, token).ConfigureAwait(false);
            if (current == null)
                return MergeDeliveryRecordResult.Refused(null, NotFoundCode, "Merge entry not found");
            if (current.Status != MergeStatusEnum.Failed)
                return MergeDeliveryRecordResult.Refused(current, NotFailedCode,
                    "Merge entry " + current.Id + " is " + current.Status + "; delivery proof is recorded only on a Failed entry.");
            if (current.DeliveryEvidence != null)
                return MergeDeliveryRecordResult.Refused(current, AlreadyRecordedCode,
                    "Merge entry " + current.Id + " already carries delivery proof: " + current.DeliveryEvidence.Describe());

            current.DeliveryEvidence = new LandingReconcileEvidence
            {
                MissionId = mission?.Id ?? String.Empty,
                VesselId = vessel.Id,
                EvidenceCommit = wanted,
                SourceCommit = source,
                EvidenceKind = proofKind,
                TargetRef = target,
                ProposedUtc = DateTime.UtcNow,
                RecordedBy = recordedBy,
                IncidentId = String.IsNullOrWhiteSpace(incidentId) ? null : incidentId.Trim()
            };
            await _Database.MergeEntries.UpdateAsync(current, token).ConfigureAwait(false);

            ArmadaEvent recorded = new ArmadaEvent(RecordedEventType,
                "Failed merge " + current.Id + " carries work already on " + target + ": " + current.DeliveryEvidence.Describe());
            recorded.EntityType = "merge_entry";
            recorded.EntityId = current.Id;
            recorded.VesselId = current.VesselId;
            recorded.TenantId = current.TenantId;
            recorded.UserId = current.UserId;
            await _Database.Events.CreateAsync(recorded, token).ConfigureAwait(false);
            return MergeDeliveryRecordResult.Stored(current, current.DeliveryEvidence.Describe());
        }

        #endregion
    }
}
