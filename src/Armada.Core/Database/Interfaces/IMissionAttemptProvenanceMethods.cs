namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Per-attempt provenance records, one per captain process attempt at a mission.</summary>
    public interface IMissionAttemptProvenanceMethods
    {
        /// <summary>Store the record of an attempt that has launched.</summary>
        /// <param name="record">Record to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored record.</returns>
        Task<MissionAttemptProvenance> CreateAsync(MissionAttemptProvenance record, CancellationToken token = default);

        /// <summary>Read one record by its identifier.</summary>
        /// <param name="id">Record identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The record, or null if absent.</returns>
        Task<MissionAttemptProvenance?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Close the running record of one process of a mission. A record that already ended is left as it is.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="processId">Operating-system process identifier the record was started with.</param>
        /// <param name="outcome">How the attempt ended.</param>
        /// <param name="exitCode">Exit code, when the runtime reported one.</param>
        /// <param name="reasonCode">Optional machine reason code.</param>
        /// <param name="endedUtc">UTC time the attempt ended.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of records closed.</returns>
        Task<int> CloseAsync(string missionId, int processId, MissionAttemptOutcomeEnum outcome, int? exitCode, string? reasonCode, DateTime endedUtc, CancellationToken token = default);

        /// <summary>
        /// Close every running record of a mission as interrupted. A new attempt calls it first, so a mission has at
        /// most one running record.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="reasonCode">Machine reason code.</param>
        /// <param name="endedUtc">UTC time the records ended.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of records closed.</returns>
        Task<int> InterruptRunningAsync(string missionId, string reasonCode, DateTime endedUtc, CancellationToken token = default);

        /// <summary>Read the newest records for one exact tenant and mission.</summary>
        /// <param name="tenantId">Exact tenant identifier; null matches a record with no tenant.</param>
        /// <param name="missionId">Exact mission identifier.</param>
        /// <param name="limit">Maximum rows to return, clamped to [1, 1000].</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The newest bounded set in ascending start order, with a truncation flag.</returns>
        Task<ProductionFactPage<MissionAttemptProvenance>> EnumerateByMissionAsync(string? tenantId, string missionId, int limit = 100, CancellationToken token = default);
    }
}
