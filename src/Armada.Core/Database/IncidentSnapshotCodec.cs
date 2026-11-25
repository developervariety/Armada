namespace Armada.Core.Database
{
    using System;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Models;

    /// <summary>
    /// The payload form of an incident snapshot, shared by the event writer, the projection row and the backfill, so
    /// an incident reads the same from a snapshot event and from its projection row.
    /// </summary>
    internal static class IncidentSnapshotCodec
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Serialize an incident as a snapshot payload.
        /// </summary>
        internal static string Serialize(Incident incident)
        {
            return JsonSerializer.Serialize(incident, _JsonOptions);
        }

        /// <summary>
        /// Read an incident from a snapshot payload. Ownership and update time the payload lacks come from the
        /// snapshot, and an incident written before opened reasons were kept reads its root cause as the opened reason
        /// unless a person is recorded as its author.
        /// </summary>
        /// <param name="payload">Snapshot payload.</param>
        /// <param name="snapshotTenantId">Tenant of the snapshot event.</param>
        /// <param name="snapshotUserId">User of the snapshot event.</param>
        /// <param name="snapshotCreatedUtc">Creation time of the snapshot event.</param>
        /// <returns>The incident, or null when the payload is empty or holds no incident.</returns>
        internal static Incident? Deserialize(string? payload, string? snapshotTenantId, string? snapshotUserId, DateTime snapshotCreatedUtc)
        {
            if (String.IsNullOrWhiteSpace(payload)) return null;

            using JsonDocument snapshot = JsonDocument.Parse(payload);
            Incident? incident = snapshot.RootElement.Deserialize<Incident>(_JsonOptions);
            if (incident == null) return null;

            incident.TenantId = incident.TenantId ?? snapshotTenantId;
            incident.UserId = incident.UserId ?? snapshotUserId;
            incident.LastUpdateUtc = incident.LastUpdateUtc == default ? snapshotCreatedUtc : incident.LastUpdateUtc;

            // An explicit null is a recorded absence of opening text. Only older snapshots that
            // lack the field need the fallback; otherwise a later automatic cause becomes its own
            // opening reason on read and can never qualify an automatic close.
            if (incident.OpenedReason == null && incident.RootCauseWrittenBy == null
                && !snapshot.RootElement.EnumerateObject().Any(property =>
                    String.Equals(property.Name, nameof(Incident.OpenedReason), StringComparison.OrdinalIgnoreCase)))
                incident.OpenedReason = incident.RootCause;
            return incident;
        }
    }
}
