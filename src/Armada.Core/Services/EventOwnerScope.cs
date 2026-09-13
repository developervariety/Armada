namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Gives an event its owner's tenant and user. Scoped event reads filter on both, so an event written without
    /// them is visible only to an unscoped administrator. Every event writer that has an owning record uses this
    /// one rule instead of copying the assignment.
    /// </summary>
    public static class EventOwnerScope
    {
        #region Public-Methods

        /// <summary>
        /// Scope an event to a mission already in hand.
        /// </summary>
        /// <param name="evt">Event to scope.</param>
        /// <param name="mission">Owning mission.</param>
        public static void ApplyFromMission(ArmadaEvent evt, Mission mission)
        {
            if (evt == null) throw new ArgumentNullException(nameof(evt));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            evt.TenantId = mission.TenantId;
            evt.UserId = mission.UserId;
        }

        /// <summary>
        /// Resolve and apply the event's owner from the records it references, in order: mission, voyage, vessel,
        /// captain, then the entity the event is about. The first record that carries a tenant supplies the owner.
        /// An event that already carries a tenant is left unchanged.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="evt">Event to scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Outcome with its source or reason; never null.</returns>
        public static async Task<EventOwnerScopeResult> ApplyAsync(DatabaseDriver database, ArmadaEvent evt, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (evt == null) throw new ArgumentNullException(nameof(evt));

            if (!String.IsNullOrEmpty(evt.TenantId))
                return new EventOwnerScopeResult(EventOwnerScopeOutcomeEnum.AlreadyScoped, "event already carries a tenant");

            try
            {
                if (await TryOwnerAsync(database, evt, "mission", evt.MissionId, token).ConfigureAwait(false))
                    return new EventOwnerScopeResult(EventOwnerScopeOutcomeEnum.Scoped, "mission");
                if (await TryOwnerAsync(database, evt, "voyage", evt.VoyageId, token).ConfigureAwait(false))
                    return new EventOwnerScopeResult(EventOwnerScopeOutcomeEnum.Scoped, "voyage");
                if (await TryOwnerAsync(database, evt, "vessel", evt.VesselId, token).ConfigureAwait(false))
                    return new EventOwnerScopeResult(EventOwnerScopeOutcomeEnum.Scoped, "vessel");
                if (await TryOwnerAsync(database, evt, "captain", evt.CaptainId, token).ConfigureAwait(false))
                    return new EventOwnerScopeResult(EventOwnerScopeOutcomeEnum.Scoped, "captain");

                string? entityType = evt.EntityType?.Trim().ToLowerInvariant();
                if (entityType != null
                    && await TryOwnerAsync(database, evt, entityType, evt.EntityId, token).ConfigureAwait(false))
                    return new EventOwnerScopeResult(EventOwnerScopeOutcomeEnum.Scoped, "entity " + entityType);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new EventOwnerScopeResult(EventOwnerScopeOutcomeEnum.LookupFailed, "owner lookup failed (" + ex.GetType().Name + "): " + ex.Message);
            }

            return new EventOwnerScopeResult(EventOwnerScopeOutcomeEnum.NoOwnerRecord, "no referenced record exists or carries a tenant");
        }

        #endregion

        #region Private-Methods

        private static async Task<bool> TryOwnerAsync(DatabaseDriver database, ArmadaEvent evt, string kind, string? id, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(id)) return false;

            string? tenantId = null;
            string? userId = null;
            switch (kind)
            {
                case "mission":
                    Mission? mission = await database.Missions.ReadAsync(id, token).ConfigureAwait(false);
                    tenantId = mission?.TenantId;
                    userId = mission?.UserId;
                    break;
                case "voyage":
                    Voyage? voyage = await database.Voyages.ReadAsync(id, token).ConfigureAwait(false);
                    tenantId = voyage?.TenantId;
                    userId = voyage?.UserId;
                    break;
                case "vessel":
                    Vessel? vessel = await database.Vessels.ReadAsync(id, token).ConfigureAwait(false);
                    tenantId = vessel?.TenantId;
                    userId = vessel?.UserId;
                    break;
                case "captain":
                    Captain? captain = await database.Captains.ReadAsync(id, token).ConfigureAwait(false);
                    tenantId = captain?.TenantId;
                    userId = captain?.UserId;
                    break;
                case "fleet":
                    Fleet? fleet = await database.Fleets.ReadAsync(id, token).ConfigureAwait(false);
                    tenantId = fleet?.TenantId;
                    userId = fleet?.UserId;
                    break;
                case "dock":
                    Dock? dock = await database.Docks.ReadAsync(id, token).ConfigureAwait(false);
                    tenantId = dock?.TenantId;
                    userId = dock?.UserId;
                    break;
                default:
                    return false;
            }

            if (String.IsNullOrEmpty(tenantId)) return false;
            evt.TenantId = tenantId;
            evt.UserId = userId;
            return true;
        }

        #endregion
    }
}
