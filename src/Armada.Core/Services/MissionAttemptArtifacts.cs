namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>Separates replay launch artifacts from the retained files of prior attempts.</summary>
    public static class MissionAttemptArtifacts
    {
        /// <summary>Resolve the current artifact stem without falling back to a prior Research attempt.</summary>
        /// <param name="missionId">Durable mission identity.</param>
        /// <param name="replayId">Durable replay generation, or null for the original attempt.</param>
        /// <returns>A distinct file stem for each replay generation.</returns>
        public static string Stem(string missionId, string? replayId)
        {
            if (String.IsNullOrEmpty(replayId)) return missionId;
            if (!Regex.IsMatch(replayId, @"^rpl_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Invalid persisted Research replay identity.");
            return missionId + "." + replayId;
        }

        /// <summary>Resolve a mission's current raw-log filename.</summary>
        /// <param name="logDirectory">Configured private log root.</param>
        /// <param name="mission">Current durable mission.</param>
        /// <returns>Current raw-log path; prior paths stay unchanged.</returns>
        public static string LogPath(string logDirectory, Mission mission)
            => Path.Combine(logDirectory, "missions", Stem(mission.Id, mission.ResearchReplayId) + ".log");
    }
}
