namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Finds the vessel whose working checkout holds the running server. Self-deploy and build-drift
    /// reporting both ask this one rule, so they cannot disagree about which vessel is the server itself.
    /// </summary>
    public static class SelfVesselLocator
    {
        #region Public-Methods

        /// <summary>
        /// The first vessel whose working directory is <paramref name="baseDirectory"/> or contains it. Letter
        /// case is compared the way the host's default file system does (<see cref="PathContainment.IsWithinHostCasing"/>),
        /// because a refused match here silently turns the feature off.
        /// </summary>
        /// <param name="vessels">Candidate vessels.</param>
        /// <param name="baseDirectory">Directory the running server was loaded from.</param>
        /// <returns>The matching vessel, or null when none holds the server.</returns>
        public static Vessel? FindByBaseDirectory(IEnumerable<Vessel> vessels, string? baseDirectory)
        {
            if (vessels == null) throw new ArgumentNullException(nameof(vessels));
            if (String.IsNullOrWhiteSpace(baseDirectory)) return null;

            foreach (Vessel vessel in vessels)
            {
                if (String.IsNullOrWhiteSpace(vessel.WorkingDirectory)) continue;
                if (PathContainment.IsWithinHostCasing(vessel.WorkingDirectory, baseDirectory)) return vessel;
            }

            return null;
        }

        #endregion
    }
}
