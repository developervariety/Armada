namespace Armada.Test.Common
{
    using System;
    using System.IO;
    using Armada.Core;

    /// <summary>
    /// Redirects the default Armada data directory to a per-run temp path for a test process.
    ///
    /// Settings objects constructed without an explicit DataDirectory resolve their repos, docks,
    /// logs, and database path from <see cref="Constants.DefaultDataDirectory"/>, which normally
    /// points at the live Armada home. A test that drives dock, captain, or mission services then
    /// writes into the same tree that holds the real bare repos, docks, settings, and merge queue.
    /// Database rows are already isolated by the test database; the filesystem was not.
    ///
    /// Call <see cref="Redirect"/> as the FIRST statement of a test entry point.
    /// <see cref="Constants.DefaultDataDirectory"/> is resolved once at type initialization, so any
    /// earlier access to that type pins the real home and the redirect silently does nothing.
    /// <see cref="Verify"/> exists to make that failure loud rather than silent.
    /// </summary>
    public static class TestDataDirectory
    {
        #region Public-Members

        /// <summary>
        /// Temp root this process was redirected to, or null when <see cref="Redirect"/> has not run.
        /// </summary>
        public static string? Root => _Root;

        #endregion

        #region Private-Members

        private static string? _Root = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Point the default data directory at a fresh temp path for this process. Safe to call
        /// more than once; only the first call takes effect. Returns the temp root.
        /// </summary>
        public static string Redirect()
        {
            if (!String.IsNullOrEmpty(_Root)) return _Root!;

            string root = Path.Combine(
                Path.GetTempPath(),
                "armada-test-home-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            // Referencing the variable NAME is a compile-time constant, so this does not trigger
            // static initialization of Constants and pin the real home before the redirect lands.
            Environment.SetEnvironmentVariable(Constants.DataDirectoryOverrideVariable, root);

            _Root = root;
            return root;
        }

        /// <summary>
        /// Throw when the redirect did not take effect, which means something touched
        /// <see cref="Constants.DefaultDataDirectory"/> before <see cref="Redirect"/> ran and the
        /// process is pointed at the real Armada home.
        /// </summary>
        public static void Verify()
        {
            if (String.IsNullOrEmpty(_Root))
                throw new InvalidOperationException("TestDataDirectory.Redirect was never called.");

            string resolved = Constants.DefaultDataDirectory;
            if (!resolved.StartsWith(_Root!, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Armada default data directory resolved to '" + resolved + "' instead of the test temp root '" +
                    _Root + "'. Something touched Armada.Core.Constants before TestDataDirectory.Redirect ran, so this " +
                    "test process would write into the live Armada home.");
            }
        }

        /// <summary>
        /// Remove the temp root. Best-effort; a leftover temp directory is not worth failing a run.
        /// </summary>
        public static void Cleanup()
        {
            if (String.IsNullOrEmpty(_Root)) return;

            try
            {
                if (Directory.Exists(_Root!)) Directory.Delete(_Root!, true);
            }
            catch
            {
            }
        }

        #endregion
    }
}
