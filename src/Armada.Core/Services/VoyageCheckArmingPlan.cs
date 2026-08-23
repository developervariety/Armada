namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Decides which Checks a freshly dispatched voyage should be armed with.
    /// </summary>
    /// <remarks>
    /// Checks are armed as Pending rather than executed, because at dispatch there is no branch and
    /// no commit to run against and no work to measure. An armed record is therefore an INTENT
    /// MARKER: it declares that this voyage wants a Build and a UnitTest gate, and it is executed
    /// once the voyage completes and its work is on the default branch.
    /// <para>
    /// An intent marker does not satisfy the real-signal gate and is not waited on by it. It holds
    /// no command output, so it can vouch for nothing; and because it carries no branch, running it
    /// early would measure the default branch rather than the work under review, which is a green
    /// that means less than no green at all. The gate reads only records that actually ran.
    /// </para>
    /// <para>
    /// A type already attached to the voyage is never armed again, whatever its state. Adding a
    /// second Build beside a failed one would leave the voyage carrying a green and a red, and a
    /// single failed Check rejects a Judge PASS however many green ones sit next to it - so
    /// re-arming would manufacture exactly the condition an operator has to clean up by hand.
    /// </para>
    /// </remarks>
    public static class VoyageCheckArmingPlan
    {
        #region Public-Methods

        /// <summary>
        /// Resolve the Check types to arm for a voyage.
        /// </summary>
        /// <param name="settings">Arming configuration. Null disables arming.</param>
        /// <param name="profile">
        /// The vessel's resolved workflow profile. A type is only armed when the profile actually
        /// defines the command for it, because a Check with no command cannot produce a real
        /// signal and would sit Pending until it failed the Judge.
        /// </param>
        /// <param name="existingVoyageChecks">Checks already attached to the voyage, if any.</param>
        /// <returns>The types to create, in a stable order. Empty when nothing should be armed.</returns>
        public static IReadOnlyList<CheckRunTypeEnum> Resolve(
            VoyageCheckArmingSettings? settings,
            WorkflowProfile? profile,
            IEnumerable<CheckRun>? existingVoyageChecks)
        {
            List<CheckRunTypeEnum> planned = new List<CheckRunTypeEnum>();

            if (settings == null || !settings.Enabled) return planned;
            if (profile == null) return planned;

            HashSet<CheckRunTypeEnum> alreadyAttached = new HashSet<CheckRunTypeEnum>();
            if (existingVoyageChecks != null)
            {
                foreach (CheckRun existing in existingVoyageChecks)
                {
                    if (existing == null) continue;
                    alreadyAttached.Add(existing.Type);
                }
            }

            if (settings.ArmBuild
                && !String.IsNullOrWhiteSpace(profile.BuildCommand)
                && !alreadyAttached.Contains(CheckRunTypeEnum.Build))
            {
                planned.Add(CheckRunTypeEnum.Build);
            }

            if (settings.ArmUnitTest
                && !String.IsNullOrWhiteSpace(profile.UnitTestCommand)
                && !alreadyAttached.Contains(CheckRunTypeEnum.UnitTest))
            {
                planned.Add(CheckRunTypeEnum.UnitTest);
            }

            return planned;
        }

        #endregion
    }
}
