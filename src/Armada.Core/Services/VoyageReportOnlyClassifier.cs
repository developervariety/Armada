namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Classifies voyages whose every mission is read-only. Such voyages skip code Check arming and
    /// the Build/UnitTest real-signal gates; a Judge PASS is accepted on report evidence alone.
    /// </summary>
    /// <remarks>
    /// Fully report-only means every mission is Audit, or every mission is Research. A voyage that
    /// mixes Audit with Research, or carries any Implementation mission, keeps the code Check gates.
    /// </remarks>
    public static class VoyageReportOnlyClassifier
    {
        #region Public-Methods

        /// <summary>
        /// True when every mission on the voyage is Audit, or every mission is Research.
        /// </summary>
        /// <param name="missions">Missions on the voyage. Null or empty returns false.</param>
        /// <returns>True when the voyage is fully report-only.</returns>
        public static bool IsFullyReportOnly(IReadOnlyList<Mission>? missions)
        {
            if (missions == null || missions.Count == 0) return false;
            return IsFullyReportOnlyModes(CollectModes(missions));
        }

        /// <summary>
        /// True when every supplied mode is Audit, or every supplied mode is Research.
        /// </summary>
        /// <param name="modes">Mission modes for the voyage. Null or empty returns false.</param>
        /// <returns>True when the mode set is fully report-only.</returns>
        public static bool IsFullyReportOnlyModes(IReadOnlyList<MissionModeEnum>? modes)
        {
            if (modes == null || modes.Count == 0) return false;

            bool allAudit = true;
            bool allResearch = true;
            foreach (MissionModeEnum mode in modes)
            {
                if (mode != MissionModeEnum.Audit) allAudit = false;
                if (mode != MissionModeEnum.Research) allResearch = false;
            }

            return allAudit || allResearch;
        }

        #endregion

        #region Private-Methods

        private static List<MissionModeEnum> CollectModes(IReadOnlyList<Mission> missions)
        {
            List<MissionModeEnum> modes = new List<MissionModeEnum>(missions.Count);
            foreach (Mission mission in missions)
            {
                if (mission == null) continue;
                modes.Add(mission.Mode);
            }

            return modes;
        }

        #endregion
    }
}
