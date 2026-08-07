namespace Armada.Test.Shared.Infrastructure
{
    using System;
    using Armada.Core.Services;

    /// <summary>
    /// Holds the temp test directory and the log rotation service instance for log rotation tests.
    /// </summary>
    public class LogRotationTestContext
    {
        #region Public-Members

        /// <summary>
        /// Temporary test directory path.
        /// </summary>
        public string TestDir { get; set; } = "";

        /// <summary>
        /// Log rotation service instance.
        /// </summary>
        public LogRotationService Service { get; set; } = null!;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public LogRotationTestContext()
        {
        }

        /// <summary>
        /// Instantiate with test directory and service.
        /// </summary>
        /// <param name="testDir">Temporary test directory path.</param>
        /// <param name="service">Log rotation service instance.</param>
        public LogRotationTestContext(string testDir, LogRotationService service)
        {
            TestDir = testDir ?? "";
            Service = service ?? throw new ArgumentNullException(nameof(service));
        }

        #endregion
    }
}
