namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for durable Judge follow-ups.
    /// </summary>
    public interface IJudgeFollowUpMethods
    {
        /// <summary>Create or update an item, keyed by Judge mission.</summary>
        Task<JudgeFollowUp> UpsertAsync(JudgeFollowUp followUp, CancellationToken token = default);

        /// <summary>Read an item by identifier.</summary>
        Task<JudgeFollowUp?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>Read the newest canonical follow-up associated with a merge entry.</summary>
        Task<JudgeFollowUp?> ReadByMergeEntryAsync(string mergeEntryId, CancellationToken token = default);

        /// <summary>Update an item.</summary>
        Task<JudgeFollowUp> UpdateAsync(JudgeFollowUp followUp, CancellationToken token = default);

        /// <summary>
        /// Associate an item with a merge entry only when it is still unassociated.
        /// </summary>
        /// <returns>True when this caller created the association.</returns>
        Task<bool> TryAssociateAsync(string followUpId, string mergeEntryId, CancellationToken token = default);

        /// <summary>Complete an audit without changing delivery-association fields.</summary>
        Task<JudgeFollowUp> CompleteAuditAsync(
            string id,
            string verdict,
            string notes,
            string? recommendedAction,
            DateTime completedUtc,
            CancellationToken token = default);

        /// <summary>Enumerate pending items, optionally for one vessel, oldest first.</summary>
        Task<List<JudgeFollowUp>> EnumeratePendingAsync(string? vesselId = null, CancellationToken token = default);

        /// <summary>Enumerate all unassociated items, optionally for one vessel, oldest first.</summary>
        Task<List<JudgeFollowUp>> EnumerateUnassociatedAsync(string? vesselId = null, CancellationToken token = default);

        /// <summary>Enumerate items not yet associated with a merge entry for one reviewed mission.</summary>
        Task<List<JudgeFollowUp>> EnumerateUnassociatedByReviewedMissionAsync(string reviewedMissionId, CancellationToken token = default);

        /// <summary>Enumerate unassociated items produced by one Judge mission.</summary>
        Task<List<JudgeFollowUp>> EnumerateUnassociatedByJudgeMissionAsync(string judgeMissionId, CancellationToken token = default);
    }
}
