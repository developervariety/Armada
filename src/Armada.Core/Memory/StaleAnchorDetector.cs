namespace Armada.Core.Memory
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Checks accepted reflection memory anchors against current vessel code/index evidence.
    /// Returns staleness warnings without mutating any data.
    /// </summary>
    public static class StaleAnchorDetector
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Inspects reflection.accepted events for the given vessel and returns staleness warnings.
        /// Flags missing files (when vessel.LocalPath is set) and missing source missions.
        /// Read-only: never mutates playbooks or events.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="vesselId">Vessel to scope the check.</param>
        /// <param name="limit">Maximum number of reflection.accepted events to inspect.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Detection result with warnings; empty warnings list when all anchors are current.</returns>
        public static async Task<StaleAnchorDetectionResult> DetectAsync(
            DatabaseDriver database,
            string vesselId,
            int limit = 200,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));

            StaleAnchorDetectionResult result = new StaleAnchorDetectionResult();
            result.VesselId = vesselId;

            Vessel? vessel = await database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            bool hasLocalPath = vessel != null && !String.IsNullOrWhiteSpace(vessel.LocalPath);
            result.FileChecksAvailable = hasLocalPath;
            if (!hasLocalPath)
                result.SkipReason = "no_local_path";

            List<ArmadaEvent> events = await database.Events.EnumerateByVesselAsync(vesselId, limit, token).ConfigureAwait(false);

            foreach (ArmadaEvent ev in events)
            {
                if (!String.Equals(ev.EventType, "reflection.accepted", StringComparison.Ordinal))
                    continue;

                result.CheckedEventCount++;

                if (String.IsNullOrWhiteSpace(ev.Payload))
                    continue;

                AcceptedEventPayload? payload = null;
                try
                {
                    payload = JsonSerializer.Deserialize<AcceptedEventPayload>(ev.Payload!, _JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (payload?.Anchors == null)
                    continue;

                string? playbookId = payload.PlaybookId;
                string? eventMissionId = payload.MissionId;

                if (hasLocalPath)
                {
                    foreach (string filePath in payload.Anchors.FilePaths)
                    {
                        string normalizedPath = filePath.Replace('/', Path.DirectorySeparatorChar);
                        string absolutePath = Path.Combine(vessel!.LocalPath!, normalizedPath);
                        if (!File.Exists(absolutePath))
                        {
                            result.Warnings.Add(new StaleAnchorWarning
                            {
                                EventId = ev.Id,
                                PlaybookId = playbookId,
                                SourceMissionId = eventMissionId,
                                WarnKind = "missing_file",
                                AffectedPath = filePath,
                                Detail = "File anchor no longer exists at: " + absolutePath
                            });
                        }
                    }
                }

                foreach (string msnId in payload.Anchors.SourceMissionIds)
                {
                    Mission? mission = await database.Missions.ReadAsync(msnId, token).ConfigureAwait(false);
                    if (mission == null)
                    {
                        result.Warnings.Add(new StaleAnchorWarning
                        {
                            EventId = ev.Id,
                            PlaybookId = playbookId,
                            SourceMissionId = eventMissionId,
                            WarnKind = "missing_mission",
                            AffectedMissionId = msnId,
                            Detail = "Source mission anchor no longer found in database: " + msnId
                        });
                    }
                }
            }

            return result;
        }

        #endregion

        #region Private-Types

        private sealed class AcceptedEventPayload
        {
            public string? PlaybookId { get; set; }
            public string? MissionId { get; set; }
            public AnchorPayload? Anchors { get; set; }
        }

        private sealed class AnchorPayload
        {
            public List<string> SourceMissionIds { get; set; } = new List<string>();
            public List<string> FilePaths { get; set; } = new List<string>();
        }

        #endregion
    }
}
