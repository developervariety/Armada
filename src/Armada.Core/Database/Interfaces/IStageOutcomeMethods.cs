namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for stage outcomes (stage_outcomes): one row per stage attempt.
    /// </summary>
    public interface IStageOutcomeMethods
    {
        /// <summary>
        /// Create an outcome.
        /// </summary>
        /// <param name="outcome">Outcome.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created outcome.</returns>
        Task<StageOutcome> CreateAsync(StageOutcome outcome, CancellationToken token = default);

        /// <summary>
        /// Read an outcome by id (any tenant).
        /// </summary>
        /// <param name="id">Outcome identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The outcome, or null.</returns>
        Task<StageOutcome?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Update every field of an outcome except its identifier, tenant and creation time; sets LastUpdateUtc.
        /// </summary>
        /// <param name="outcome">Outcome with updated fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The updated outcome.</returns>
        Task<StageOutcome> UpdateAsync(StageOutcome outcome, CancellationToken token = default);

        /// <summary>
        /// Delete an outcome.
        /// </summary>
        /// <param name="id">Outcome identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a row was deleted.</returns>
        Task<bool> DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// List one page of outcomes, newest (EndedUtc) first, with the total count of matching rows.
        /// </summary>
        /// <param name="query">Filters and paging.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The page.</returns>
        Task<EnumerationResult<StageOutcome>> EnumerateAsync(StageOutcomeQuery query, CancellationToken token = default);

        /// <summary>
        /// Count the outcomes recorded for a mission (used to number attempts).
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The count.</returns>
        Task<int> CountByMissionAsync(string missionId, CancellationToken token = default);

        /// <summary>
        /// Delete every outcome of a mission (when the mission is purged).
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of rows deleted.</returns>
        Task<int> DeleteByMissionAsync(string missionId, CancellationToken token = default);
    }
}
