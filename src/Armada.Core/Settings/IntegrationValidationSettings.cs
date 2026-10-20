namespace Armada.Core.Settings
{
    using System.Collections.Generic;

    /// <summary>
    /// Controls validation of the actual merged commit before a landing advances or publishes a target
    /// branch.
    /// </summary>
    /// <remarks>
    /// A branch can pass every one of its own Checks and still break the target once it is merged with
    /// newer target work. Both landing paths therefore build the candidate merge and run the vessel's
    /// required checks against that exact commit before the target moves. The required checks are the
    /// Build and UnitTest commands of the vessel's resolved workflow profile. A vessel that requires no
    /// integration checks says so explicitly through <see cref="NoChecksRequiredVesselIds"/>; a vessel
    /// that requires checks but defines no command is refused with a named reason rather than landed
    /// unmeasured.
    /// </remarks>
    public class IntegrationValidationSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether landings validate the candidate merge before advancing the target. Defaults to true.
        /// When false, landings publish without integration checks and the evidence event records the
        /// outcome <c>Disabled</c>.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Vessels whose policy explicitly requires no integration checks. A landing on one of these
        /// vessels publishes without running checks and records the outcome <c>NotRequired</c>.
        /// </summary>
        public List<string> NoChecksRequiredVesselIds
        {
            get => _NoChecksRequiredVesselIds;
            set => _NoChecksRequiredVesselIds = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private List<string> _NoChecksRequiredVesselIds = new List<string>();

        #endregion
    }
}
