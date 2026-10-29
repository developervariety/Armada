namespace Armada.Core.Database
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// One row of the incident projection: an incident as its newest snapshot event records it, with the owner the
    /// read filters test and the snapshot it came from.
    /// </summary>
    internal sealed class IncidentProjection
    {
        /// <summary>The incident, read from the snapshot payload.</summary>
        internal Incident Incident { get; set; } = null!;

        /// <summary>Tenant of the snapshot event.</summary>
        internal string? TenantId { get; set; }

        /// <summary>Owning user: the incident's own user, else the user who wrote the snapshot.</summary>
        internal string? UserId { get; set; }

        /// <summary>Identifier of the snapshot event the row projects.</summary>
        internal string SnapshotId { get; set; } = String.Empty;

        /// <summary>
        /// Project a snapshot event.
        /// </summary>
        /// <param name="snapshot">Snapshot event.</param>
        /// <param name="incident">Incident the snapshot holds.</param>
        internal static IncidentProjection From(ArmadaEvent snapshot, Incident incident)
        {
            return new IncidentProjection
            {
                Incident = incident,
                TenantId = snapshot.TenantId,
                UserId = incident.UserId ?? snapshot.UserId,
                SnapshotId = snapshot.Id
            };
        }
    }
}
