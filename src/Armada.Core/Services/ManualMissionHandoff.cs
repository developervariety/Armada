namespace Armada.Core.Services
{
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>Shared REST/MCP manual handoff contract. A status write cannot substitute for landing.</summary>
    public static class ManualMissionHandoff
    {
        /// <summary>Return why a requested handoff cannot bypass the active agent or exit claim.</summary>
        public static string? Refusal(Mission mission, MissionStatusEnum target)
        {
            if (mission.Status == MissionStatusEnum.Verifying && target != MissionStatusEnum.Cancelled)
                return "Stage verification still owns this mission. Wait for it to finish, or cancel the mission.";
            if (target != MissionStatusEnum.WorkProduced && target != MissionStatusEnum.Complete) return null;
            if (mission.Status == MissionStatusEnum.Verifying || mission.Status == MissionStatusEnum.InProgress
                || mission.Status == MissionStatusEnum.Testing || mission.ProcessId.HasValue)
                return "The agent or its exit/stage-check handoff still owns this mission. Wait for normal handoff before requesting landing.";
            return null;
        }

        /// <summary>Capture the branch head, conditionally claim manual handoff, then invoke the normal landing handler.</summary>
        public static async Task<Mission> LandAsync(DatabaseDriver database, IAdmiralService admiral, Mission mission)
        {
            if (admiral.OnMissionComplete == null || admiral.OnCaptureDiff == null)
                throw new InvalidOperationException("Landing is not available; mission status was not changed.");
            Dock? dock = String.IsNullOrEmpty(mission.DockId) ? null : await database.Docks.ReadAsync(mission.DockId).ConfigureAwait(false);
            if (dock == null || !dock.Active || String.IsNullOrEmpty(dock.WorktreePath))
                throw new InvalidOperationException("An active dock is required to resolve and land this mission's branch head.");
            DateTime version = mission.LastUpdateUtc;
            MissionStatusEnum status = mission.Status;
            await admiral.OnCaptureDiff(mission, dock).ConfigureAwait(false);
            if (String.IsNullOrEmpty(mission.CommitHash))
                throw new InvalidOperationException("The mission branch head could not be resolved; mission status was not changed.");
            // CaptureDiff can persist metadata. Re-read that version, while retaining the original status/attempt guard.
            Mission? current = await database.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
            if (current == null || current.Status != status || current.ProcessId != mission.ProcessId || current.StartedUtc != mission.StartedUtc)
                throw new InvalidOperationException("Mission changed during manual handoff; retry against its current state.");
            version = current.LastUpdateUtc;
            current.CommitHash = mission.CommitHash;
            current.Status = MissionStatusEnum.WorkProduced;
            current.LastUpdateUtc = DateTime.UtcNow;
            if (!await database.Missions.TryUpdateIfUnchangedAsync(current, version, new[] { status }).ConfigureAwait(false))
                throw new InvalidOperationException("Mission changed during manual handoff; its newer state was preserved.");
            await admiral.OnMissionComplete(current, dock).ConfigureAwait(false);
            return await database.Missions.ReadAsync(current.Id).ConfigureAwait(false) ?? current;
        }
    }
}
