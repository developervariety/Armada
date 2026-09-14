namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Explicit request to push one local branch of a vessel's landing repository to its origin.
    /// </summary>
    public class BranchPushRequest
    {
        /// <summary>Local source branch name in the landing repository.</summary>
        public string? SourceRef { get; set; }

        /// <summary>Remote target branch name.</summary>
        public string? TargetRef { get; set; }

        /// <summary>Remote name. Only the vessel's configured origin is accepted.</summary>
        public string? Remote { get; set; }
    }

    /// <summary>
    /// Explicit request to merge one local branch into another inside a vessel's landing repository.
    /// </summary>
    public class BranchMergeRequest
    {
        /// <summary>Source branch name.</summary>
        public string? SourceRef { get; set; }

        /// <summary>Target branch name.</summary>
        public string? TargetRef { get; set; }

        /// <summary>Merge strategy: FastForward or MergeCommit.</summary>
        public string? Strategy { get; set; }
    }

    /// <summary>
    /// Availability of branch write controls for the requesting caller.
    /// </summary>
    public class BranchWriteControls
    {
        /// <summary>Whether the merge action can be requested.</summary>
        public bool MergeAvailable { get; set; }

        /// <summary>Reason code when merge is unavailable.</summary>
        public string? MergeUnavailableReason { get; set; }

        /// <summary>Whether the push action can be requested.</summary>
        public bool PushAvailable { get; set; }

        /// <summary>Reason code when push is unavailable.</summary>
        public string? PushUnavailableReason { get; set; }

        /// <summary>The only remote a push may target.</summary>
        public string Remote { get; set; } = "origin";
    }

    /// <summary>
    /// Outcome of a branch push or merge. A refusal names its reason and changes no refs.
    /// </summary>
    public class BranchWriteResult
    {
        /// <summary>Whether the write happened and was verified.</summary>
        public bool Succeeded { get; set; }

        /// <summary>Operation: push or merge.</summary>
        public string Operation { get; set; } = String.Empty;

        /// <summary>Refusal or failure reason code; null on success.</summary>
        public string? Reason { get; set; }

        /// <summary>Human-readable outcome.</summary>
        public string Message { get; set; } = String.Empty;

        /// <summary>Vessel identifier.</summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>Requested source branch.</summary>
        public string? SourceRef { get; set; }

        /// <summary>Requested target branch.</summary>
        public string? TargetRef { get; set; }

        /// <summary>Remote used by a push.</summary>
        public string? Remote { get; set; }

        /// <summary>Merge strategy used by a merge.</summary>
        public string? Strategy { get; set; }

        /// <summary>Source commit that was written.</summary>
        public string? SourceCommit { get; set; }

        /// <summary>Target commit before the write; null when the target did not exist.</summary>
        public string? PreviousTargetCommit { get; set; }

        /// <summary>Verified target commit after the write.</summary>
        public string? TargetCommit { get; set; }

        /// <summary>What happened to the vessel working checkout after a merge.</summary>
        public string? WorkingCheckoutSync { get; set; }
    }

    /// <summary>
    /// Reason codes for refused or failed branch writes.
    /// </summary>
    public static class BranchWriteReasons
    {
        /// <summary>Request body is missing or incomplete.</summary>
        public const string InvalidRequest = "invalid_request";
        /// <summary>A ref name is not a valid branch name.</summary>
        public const string InvalidRef = "invalid_ref";
        /// <summary>Source and target name the same branch.</summary>
        public const string SameRef = "same_ref";
        /// <summary>Merge strategy is missing or unknown.</summary>
        public const string InvalidStrategy = "invalid_strategy";
        /// <summary>Caller is not an administrator for the vessel's tenant.</summary>
        public const string AdministratorRequired = "administrator_required";
        /// <summary>Branch write service is not configured.</summary>
        public const string Unavailable = "unavailable";
        /// <summary>Vessel landing repository is missing or unreadable.</summary>
        public const string RepositoryMissing = "repository_missing";
        /// <summary>Configured working checkout does not exist.</summary>
        public const string WorkingCheckoutMissing = "working_checkout_missing";
        /// <summary>Working checkout has a detached or unreadable HEAD.</summary>
        public const string WorkingCheckoutDetached = "working_checkout_detached";
        /// <summary>Working checkout has uncommitted changes.</summary>
        public const string WorkingCheckoutDirty = "working_checkout_dirty";
        /// <summary>Source branch does not exist.</summary>
        public const string SourceMissing = "source_missing";
        /// <summary>Merge target branch does not exist.</summary>
        public const string TargetMissing = "target_missing";
        /// <summary>Merge target is checked out in a worktree of the landing repository.</summary>
        public const string TargetCheckedOut = "target_checked_out";
        /// <summary>Target matches protected branch policy.</summary>
        public const string ProtectedTarget = "protected_target";
        /// <summary>Release branches must land through the merge queue.</summary>
        public const string ReleaseRequiresMergeQueue = "release_requires_merge_queue";
        /// <summary>Source is the branch of a mission that has not completed.</summary>
        public const string MissionBranchNotLanded = "mission_branch_not_landed";
        /// <summary>Source has an active merge-queue entry.</summary>
        public const string MergeQueueEntryActive = "merge_queue_entry_active";
        /// <summary>Another landing or branch write holds the vessel.</summary>
        public const string VesselBusy = "vessel_busy";
        /// <summary>Remote is not the vessel's origin.</summary>
        public const string RemoteNotAllowed = "remote_not_allowed";
        /// <summary>Landing repository has no origin remote.</summary>
        public const string RemoteMissing = "remote_missing";
        /// <summary>Origin does not match the vessel repository URL.</summary>
        public const string RemoteMismatch = "remote_mismatch";
        /// <summary>Remote state could not be read.</summary>
        public const string RemoteUnreadable = "remote_unreadable";
        /// <summary>Write would not preserve the target's history.</summary>
        public const string NonFastForward = "non_fast_forward";
        /// <summary>Target already contains the source.</summary>
        public const string NothingToWrite = "nothing_to_write";
        /// <summary>Merge has content conflicts.</summary>
        public const string MergeConflict = "merge_conflict";
        /// <summary>Target moved while the write was prepared.</summary>
        public const string TargetMoved = "target_moved";
        /// <summary>Remote rejected the push.</summary>
        public const string PushRejected = "push_rejected";
        /// <summary>Post-write ancestry or tip verification failed.</summary>
        public const string VerificationFailed = "verification_failed";
        /// <summary>A git command failed unexpectedly.</summary>
        public const string GitFailed = "git_failed";
    }
}
