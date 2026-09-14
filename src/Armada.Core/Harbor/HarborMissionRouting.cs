namespace Armada.Core.Harbor
{
    using System;
    using System.IO;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// The one rule that decides whether a mission launch runs on a Harbor runner. The default is local: a launch
    /// routes to a runner only while Harbor is enabled and a route names the captain, or names the mission's vessel
    /// with no captain route for that captain.
    /// </summary>
    public static class HarborMissionRouting
    {
        #region Public-Members

        /// <summary>Refusal when the dock path is missing.</summary>
        public const string ReasonWorkingDirectoryMissing = "harbor_working_directory_missing";

        /// <summary>Refusal when a route sets only one of its two working-directory roots.</summary>
        public const string ReasonWorkingDirectoryMapIncomplete = "harbor_working_directory_map_incomplete";

        /// <summary>Refusal when the dock is outside the route's Admiral-side root.</summary>
        public const string ReasonWorkingDirectoryUnmapped = "harbor_working_directory_unmapped";

        #endregion

        #region Public-Methods

        /// <summary>Resolve the route for a mission launch.</summary>
        /// <param name="settings">Harbor settings.</param>
        /// <param name="captain">Captain being launched.</param>
        /// <param name="mission">Mission being launched.</param>
        /// <returns>The route, or null to launch locally.</returns>
        public static HarborMissionRoute? Resolve(HarborSettings? settings, Captain captain, Mission mission)
        {
            if (settings == null || !settings.Enabled || settings.MissionRoutes == null) return null;
            if (captain == null || mission == null) return null;

            HarborMissionRoute? vesselRoute = null;
            foreach (HarborMissionRoute route in settings.MissionRoutes)
            {
                if (route == null || String.IsNullOrWhiteSpace(route.RunnerId)) continue;
                if (!String.IsNullOrWhiteSpace(route.CaptainId))
                {
                    if (String.Equals(route.CaptainId.Trim(), captain.Id, StringComparison.Ordinal)) return route;
                    continue;
                }
                if (vesselRoute == null
                    && !String.IsNullOrWhiteSpace(route.VesselId)
                    && String.Equals(route.VesselId.Trim(), mission.VesselId, StringComparison.Ordinal))
                    vesselRoute = route;
            }
            return vesselRoute;
        }

        /// <summary>Map the Admiral's dock path to the path the runner uses.</summary>
        /// <param name="route">Route being applied.</param>
        /// <param name="admiralPath">Dock worktree path on the Admiral.</param>
        /// <param name="runnerPath">Path the runner uses.</param>
        /// <param name="failureReason">Stable refusal reason.</param>
        /// <returns>True when the path maps.</returns>
        public static bool TryMapWorkingDirectory(HarborMissionRoute route, string? admiralPath, out string runnerPath, out string failureReason)
        {
            if (route == null) throw new ArgumentNullException(nameof(route));
            runnerPath = String.Empty;
            failureReason = String.Empty;
            if (String.IsNullOrWhiteSpace(admiralPath))
            {
                failureReason = ReasonWorkingDirectoryMissing;
                return false;
            }

            bool hasAdmiralRoot = !String.IsNullOrWhiteSpace(route.AdmiralWorkingDirectoryRoot);
            bool hasRunnerRoot = !String.IsNullOrWhiteSpace(route.RunnerWorkingDirectoryRoot);
            if (!hasAdmiralRoot && !hasRunnerRoot)
            {
                runnerPath = admiralPath;
                return true;
            }
            if (hasAdmiralRoot != hasRunnerRoot)
            {
                failureReason = ReasonWorkingDirectoryMapIncomplete;
                return false;
            }

            string root = Path.GetFullPath(route.AdmiralWorkingDirectoryRoot!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(admiralPath);
            bool inside = String.Equals(full, root, StringComparison.Ordinal)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (!inside)
            {
                failureReason = ReasonWorkingDirectoryUnmapped;
                return false;
            }

            string relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            string runnerRoot = route.RunnerWorkingDirectoryRoot!.Trim().TrimEnd('/', '\\');
            runnerPath = relative.Length == 0 ? runnerRoot : runnerRoot + "/" + relative;
            return true;
        }

        #endregion
    }
}
