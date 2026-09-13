namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Shared resolution for landing execution and its read projection.</summary>
    public static class LandingConfigurationResolver
    {
        /// <summary>Resolve voyage, vessel and global modes, then the legacy flags.</summary>
        public static LandingConfiguration Resolve(ArmadaSettings settings, Vessel? vessel, Voyage? voyage = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            LandingModeEnum? mode = voyage?.LandingMode ?? vessel?.LandingMode ?? settings.LandingMode;
            LandingConfiguration configuration = new LandingConfiguration
            {
                LandingMode = mode,
                Source = voyage?.LandingMode != null ? LandingConfigurationSourceEnum.Voyage
                    : vessel?.LandingMode != null ? LandingConfigurationSourceEnum.Vessel
                    : settings.LandingMode != null ? LandingConfigurationSourceEnum.Global
                    : LandingConfigurationSourceEnum.Legacy,
                BranchCleanupPolicy = vessel?.BranchCleanupPolicy ?? settings.BranchCleanupPolicy
            };
            if (mode.HasValue)
            {
                configuration.AutoCreatePullRequests = mode.Value == LandingModeEnum.PullRequest;
                configuration.AutoPush = configuration.AutoCreatePullRequests || mode.Value == LandingModeEnum.LocalMerge;
                configuration.AutoMergePullRequests = configuration.AutoCreatePullRequests
                    && (voyage?.AutoMergePullRequests ?? settings.AutoMergePullRequests);
            }
            else
            {
                configuration.AutoPush = voyage?.AutoPush ?? settings.AutoPush;
                configuration.AutoCreatePullRequests = voyage?.AutoCreatePullRequests ?? settings.AutoCreatePullRequests;
                configuration.AutoMergePullRequests = voyage?.AutoMergePullRequests ?? settings.AutoMergePullRequests;
            }
            return configuration;
        }
    }
}
