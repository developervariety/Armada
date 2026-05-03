namespace Armada.Core.Enums
{
    /// <summary>
    /// Structured check-run types supported by Armada.
    /// </summary>
    public enum CheckRunTypeEnum
    {
        /// <summary>
        /// Static-analysis or linting check.
        /// </summary>
        Lint = 0,

        /// <summary>
        /// Project build or compile check.
        /// </summary>
        Build = 1,

        /// <summary>
        /// Unit-test execution.
        /// </summary>
        UnitTest = 2,

        /// <summary>
        /// Integration-test execution.
        /// </summary>
        IntegrationTest = 3,

        /// <summary>
        /// End-to-end test execution.
        /// </summary>
        E2ETest = 4,

        /// <summary>
        /// Package creation or assembly.
        /// </summary>
        Package = 5,

        /// <summary>
        /// Artifact publication.
        /// </summary>
        PublishArtifact = 6,

        /// <summary>
        /// Release versioning step.
        /// </summary>
        ReleaseVersioning = 7,

        /// <summary>
        /// Changelog generation step.
        /// </summary>
        Changelog = 8,

        /// <summary>
        /// Deployment execution.
        /// </summary>
        Deploy = 9,

        /// <summary>
        /// Rollback execution.
        /// </summary>
        Rollback = 10,

        /// <summary>
        /// Smoke-test verification.
        /// </summary>
        SmokeTest = 11,

        /// <summary>
        /// Health-check verification.
        /// </summary>
        HealthCheck = 12,

        /// <summary>
        /// Caller-specified custom command.
        /// </summary>
        Custom = 13
    }
}
