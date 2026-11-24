namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>Resolve only the immutable source of an explicitly consumed same-mission permit.</summary>
    public static class AdministrativeRestartSource
    {
        /// <summary>Read the current epoch binding, or return null for ordinary pipeline assignment.</summary>
        public static async Task<string?> ResolveAsync(DatabaseDriver database, Mission mission, Mission dependency,
            CancellationToken token = default)
        {
            EnumerationResult<ArmadaEvent> page = await database.Events.EnumerateAsync(new EnumerationQuery
            { MissionId = mission.Id, EventType = "mission.administrative_restart_consumed", PageSize = 1 }, token).ConfigureAwait(false);
            if (page.Objects.Count == 0) return null;
            ArmadaEvent evt = page.Objects[0];
            if (!OwnershipPolicy.SameTenant(evt.TenantId, mission.TenantId)
                || OwnershipPolicy.UserOfRecord(evt.UserId) != OwnershipPolicy.UserOfRecord(mission.UserId)) return null;
            AdministrativeRestartReceipt? receipt;
            try { receipt = JsonSerializer.Deserialize<AdministrativeRestartReceipt>(evt.Payload ?? String.Empty); }
            catch (JsonException ex) { throw new InvalidOperationException("administrative_restart_source_unreadable: no source fallback is permitted.", ex); }
            if (receipt == null) throw new InvalidOperationException("administrative_restart_source_unreadable: no source fallback is permitted.");
            if (receipt.OriginalCreatedUtc != mission.CreatedUtc) return null;
            if (receipt.MissionId != mission.Id || receipt.VoyageId != mission.VoyageId
                || receipt.DependencyId != mission.DependsOnMissionId || dependency.Id != receipt.DependencyId
                || dependency.CommitHash != receipt.DependencyCommit || mission.StartFromRef != receipt.SourceCommit
                || !OwnershipPolicy.SameTenant(mission.TenantId, dependency.TenantId)
                || OwnershipPolicy.UserOfRecord(mission.UserId) != OwnershipPolicy.UserOfRecord(dependency.UserId)
                || mission.VesselId != dependency.VesselId)
                throw new InvalidOperationException("administrative_restart_source_changed: current epoch bindings changed; no planner/default fallback is permitted.");
            return receipt.SourceCommit;
        }
    }
}
