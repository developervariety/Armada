namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Event method set that tells an observer about each stored event. Reads and deletes pass
    /// through unchanged. The observer runs after the write succeeds; its failure is reported to the
    /// failure callback and never fails the write, because the event record is the source of truth
    /// and the observer only fans it out.
    /// </summary>
    internal sealed class ObservedEventMethods : IEventMethods
    {
        #region Private-Members

        private readonly IEventMethods _Inner;
        private readonly Func<ArmadaEvent, CancellationToken, Task> _Observer;
        private readonly Action<ArmadaEvent, Exception> _OnFailure;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Wrap an event method set.
        /// </summary>
        /// <param name="inner">The event method set that stores events.</param>
        /// <param name="observer">Called with each stored event.</param>
        /// <param name="onFailure">Called when the observer throws.</param>
        public ObservedEventMethods(IEventMethods inner, Func<ArmadaEvent, CancellationToken, Task> observer, Action<ArmadaEvent, Exception> onFailure)
        {
            _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _Observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _OnFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<ArmadaEvent> CreateAsync(ArmadaEvent armadaEvent, CancellationToken token = default)
        {
            ArmadaEvent stored = await _Inner.CreateAsync(armadaEvent, token).ConfigureAwait(false);
            try
            {
                await _Observer(stored, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The event is already stored, so even a cancelled observer is reported, not rethrown:
                // a caller must never read a stored event as a failed write.
                _OnFailure(stored, ex);
            }

            return stored;
        }

        /// <inheritdoc />
        public Task<ArmadaEvent?> ReadAsync(string id, CancellationToken token = default)
        {
            return _Inner.ReadAsync(id, token);
        }

        /// <inheritdoc />
        public Task DeleteAsync(string id, CancellationToken token = default)
        {
            return _Inner.DeleteAsync(id, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateRecentAsync(int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateRecentAsync(limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByTypeAsync(string eventType, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByTypeAsync(eventType, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByEntityAsync(string entityType, string entityId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByEntityAsync(entityType, entityId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByCaptainAsync(string captainId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByCaptainAsync(captainId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByMissionAsync(string missionId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByMissionAsync(missionId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByVesselAsync(string vesselId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByVesselAsync(vesselId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByVoyageAsync(string voyageId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByVoyageAsync(voyageId, limit, token);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<ArmadaEvent>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            return _Inner.EnumerateAsync(query, token);
        }

        /// <inheritdoc />
        public Task<ArmadaEvent?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            return _Inner.ReadAsync(tenantId, id, token);
        }

        /// <inheritdoc />
        public Task DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            return _Inner.DeleteAsync(tenantId, id, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            return _Inner.EnumerateAsync(tenantId, token);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<ArmadaEvent>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            return _Inner.EnumerateAsync(tenantId, query, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateRecentAsync(string tenantId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateRecentAsync(tenantId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByTypeAsync(string tenantId, string eventType, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByTypeAsync(tenantId, eventType, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByEntityAsync(string tenantId, string entityType, string entityId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByEntityAsync(tenantId, entityType, entityId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByCaptainAsync(string tenantId, string captainId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByCaptainAsync(tenantId, captainId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByMissionAsync(string tenantId, string missionId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByMissionAsync(tenantId, missionId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByVesselAsync(string tenantId, string vesselId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByVesselAsync(tenantId, vesselId, limit, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateByVoyageAsync(string tenantId, string voyageId, int limit = 50, CancellationToken token = default)
        {
            return _Inner.EnumerateByVoyageAsync(tenantId, voyageId, limit, token);
        }

        /// <inheritdoc />
        public Task<ArmadaEvent?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            return _Inner.ReadAsync(tenantId, userId, id, token);
        }

        /// <inheritdoc />
        public Task DeleteAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            return _Inner.DeleteAsync(tenantId, userId, id, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            return _Inner.EnumerateAsync(tenantId, userId, token);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<ArmadaEvent>> EnumerateAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default)
        {
            return _Inner.EnumerateAsync(tenantId, userId, query, token);
        }

        #endregion
    }
}
