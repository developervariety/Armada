namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Records integration resolution without failing a merge on missing evidence metadata.</summary>
    public static class LandingMarkdownResolution
    {
        #region Public-Methods

        /// <summary>Persist the resolution event and append it to the mission's current attempt log.</summary>
        /// <param name="database">Delivery database.</param>
        /// <param name="settings">Log location.</param>
        /// <param name="missionId">Linked mission, when present.</param>
        /// <param name="vesselId">Linked vessel.</param>
        /// <param name="entityId">Landing mission or merge entry identity.</param>
        /// <param name="paths">Successfully resolved paths.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="warn">Receives each failed evidence operation.</param>
        /// <returns>The recorded summary, or an empty string when no path resolved.</returns>
        public static async Task<string> RecordAsync(DatabaseDriver database, ArmadaSettings settings,
            string? missionId, string? vesselId, string entityId, IReadOnlyList<string> paths, CancellationToken token, Action<string> warn)
        {
            if (paths.Count == 0) return String.Empty;
            Mission? mission = null;
            Vessel? vessel = null;
            try
            {
                if (!String.IsNullOrWhiteSpace(missionId)) mission = await database.Missions.ReadSummaryAsync(missionId, token).ConfigureAwait(false);
                if (!String.IsNullOrWhiteSpace(vesselId)) vessel = await database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warn.Invoke("Markdown resolution metadata read failed: " + RuntimeLogFormatter.RedactSecrets(ex.Message));
            }
            if (!String.IsNullOrWhiteSpace(missionId) && mission == null)
                warn.Invoke("Markdown resolution mission metadata is missing: " + missionId);
            string message = "Auto-resolved add-only Markdown conflicts: " + String.Join(", ", paths);
            ArmadaEvent record = new ArmadaEvent("landing.markdown_auto_resolved", message)
            {
                TenantId = mission?.TenantId ?? vessel?.TenantId,
                UserId = mission?.UserId ?? vessel?.UserId,
                EntityType = String.Equals(entityId, missionId, StringComparison.Ordinal) ? "mission" : "merge_entry",
                EntityId = entityId,
                MissionId = mission?.Id,
                VesselId = vessel?.Id
            };
            if (mission != null || vessel != null)
            {
                try { await database.Events.CreateAsync(record, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warn.Invoke("Markdown resolution event write failed: " + RuntimeLogFormatter.RedactSecrets(ex.Message));
                }
            }
            else warn.Invoke("Markdown resolution event has no verified owner metadata: " + entityId);
            if (mission != null)
            {
                try
                {
                    string path = MissionAttemptArtifacts.LogPath(settings.LogDirectory, mission!);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.AppendAllTextAsync(path, "[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                        + "] [ARMADA:ACTIVITY] " + message + Environment.NewLine, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warn.Invoke("Markdown resolution mission log write failed: " + RuntimeLogFormatter.RedactSecrets(ex.Message));
                }
            }
            return message;
        }

        #endregion
    }
}
