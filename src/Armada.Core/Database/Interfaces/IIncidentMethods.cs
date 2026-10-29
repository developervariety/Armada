namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// The incident projection: the latest state of each incident, kept in step with the incident snapshot events
    /// that remain the record. Reads filter, order and page in the database.
    /// </summary>
    public interface IIncidentMethods
    {
        /// <summary>
        /// Write a snapshot event and project it as the incident's current state, in one transaction. The projection
        /// keeps the newest snapshot by update time then event identifier, so a snapshot that is not newer is recorded
        /// as an event and leaves the projection as it was.
        /// </summary>
        /// <param name="snapshot">Snapshot event holding the serialized incident.</param>
        /// <param name="incident">Incident the snapshot holds.</param>
        /// <param name="token">Cancellation token.</param>
        Task WriteSnapshotAsync(ArmadaEvent snapshot, Incident incident, CancellationToken token = default);

        /// <summary>
        /// Read one incident the scope can see.
        /// </summary>
        /// <param name="scope">What the caller can see.</param>
        /// <param name="id">Incident identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The incident, or null when it does not exist or the scope cannot see it.</returns>
        Task<Incident?> ReadAsync(IncidentReadScope scope, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate the incidents the scope can see: filtered, ordered by update time with the incident identifier as
        /// the tie-break, and paged in the database.
        /// </summary>
        /// <param name="scope">What the caller can see.</param>
        /// <param name="query">Filters, order and page.</param>
        /// <param name="token">Cancellation token.</param>
        Task<EnumerationResult<Incident>> EnumerateAsync(IncidentReadScope scope, IncidentQuery query, CancellationToken token = default);

        /// <summary>
        /// Count the incidents the scope can see by status, and the open ones by severity.
        /// </summary>
        /// <param name="scope">What the caller can see.</param>
        /// <param name="token">Cancellation token.</param>
        Task<IncidentCountSummary> CountAsync(IncidentReadScope scope, CancellationToken token = default);

        /// <summary>
        /// Delete an incident the scope can see, with every snapshot event of it, in one transaction.
        /// </summary>
        /// <param name="scope">What the caller can see.</param>
        /// <param name="id">Incident identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the incident was deleted; false when it does not exist or the scope cannot see it.</returns>
        Task<bool> DeleteAsync(IncidentReadScope scope, string id, CancellationToken token = default);

        /// <summary>
        /// Project every incident that has snapshot events and no projection row, from its newest snapshot. A second
        /// run finds nothing to project.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The incidents projected, and the incidents whose newest snapshot could not be read.</returns>
        Task<IncidentBackfillResult> BackfillFromEventsAsync(CancellationToken token = default);
    }
}
