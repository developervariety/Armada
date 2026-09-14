namespace Test.Shared.Infrastructure
{
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Resource-pressure admission for test harnesses that build a MissionService.
    /// The production default reads live host and container memory, so an assignment test that
    /// omits admission defers whenever concurrent builds push available memory below the policy
    /// floor, and its assertions then fail for a reason no test controls. These factories keep the
    /// test's own admission policy and replace only the memory measurement.
    /// </summary>
    public static class TestResourcePressure
    {
        #region Public-Members

        /// <summary>
        /// Available memory reported when a test does not exercise memory pressure. It exceeds any
        /// value the admission setting accepts, so the memory gate admits under every policy.
        /// </summary>
        public const long AmpleAvailableMemoryBytes = 1024L * 1024L * 1024L * 1024L;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Admission that applies the settings' policy against ample available memory.
        /// </summary>
        /// <param name="settings">Settings whose admission policy the test configured.</param>
        /// <returns>Admission independent of host memory.</returns>
        public static IResourcePressureAdmission Unconstrained(ArmadaSettings settings)
        {
            return WithAvailableMemory(settings, new FixedResourcePressureProbe(AmpleAvailableMemoryBytes));
        }

        /// <summary>
        /// Admission that applies the settings' policy against the memory a probe reports.
        /// </summary>
        /// <param name="settings">Settings whose admission policy the test configured.</param>
        /// <param name="probe">Probe reporting the memory the test chose.</param>
        /// <returns>Admission independent of host memory.</returns>
        public static IResourcePressureAdmission WithAvailableMemory(ArmadaSettings settings, IResourcePressureProbe probe)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new ResourcePressureAdmission(settings.ResourcePressureAdmission, probe, logging);
        }

        #endregion
    }
}
