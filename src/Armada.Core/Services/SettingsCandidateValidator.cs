namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// The one validation contract for candidate settings, whatever their source: a settings update request, a
    /// manual reload, or a watched reload of the settings file. A candidate that fails is refused before any value
    /// is applied, so every entry point accepts and rejects the same candidates.
    /// </summary>
    public static class SettingsCandidateValidator
    {
        #region Public-Methods

        /// <summary>
        /// Validate a candidate usage-routing policy: its shape and ranges, account key paths against the live
        /// account folder root, and account runtimes against the captains each account lists.
        /// </summary>
        /// <param name="candidate">Candidate usage-routing policy.</param>
        /// <param name="dataDirectory">Live data directory; the account folder root is derived from it.</param>
        /// <param name="captains">Current captain roster.</param>
        /// <exception cref="ArgumentException">The candidate is invalid; the message names the reason.</exception>
        public static void ValidateUsageRouting(UsageRoutingSettings candidate, string dataDirectory, IEnumerable<Captain> captains)
        {
            if (candidate == null) throw new ArgumentException("Usage routing settings are required.");
            UsageRoutingService.Validate(candidate, AccountLoginPaths.AccountsRoot(dataDirectory));
            CaptainAccountLaunch.ValidateCaptainBindings(candidate, captains ?? new List<Captain>());
        }

        /// <summary>
        /// Validate a whole candidate settings instance read from a settings file before its runtime-tunable values
        /// are applied to the live settings.
        /// </summary>
        /// <param name="candidate">Candidate settings.</param>
        /// <param name="dataDirectory">Live data directory. The live value is used because paths are not reloaded.</param>
        /// <param name="captains">Current captain roster.</param>
        /// <exception cref="ArgumentException">The candidate is invalid; the message names the reason.</exception>
        public static void Validate(ArmadaSettings candidate, string dataDirectory, IEnumerable<Captain> captains)
        {
            if (candidate == null) throw new ArgumentException("Candidate settings are required.");
            ValidateUsageRouting(candidate.ModelTier.UsageRouting, dataDirectory, captains);
        }

        #endregion
    }
}
