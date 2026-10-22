namespace Armada.Test.Common
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
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
    ///
    /// The redirect also points the process temp directory (TMPDIR, TMP, TEMP) at <c>tmp/</c> under the
    /// temp root, so every temp folder or file a test or a child process makes lives under the root, and
    /// the root is removed when the process exits. A run that is killed leaves only its root behind; the
    /// next run removes such a root once its owning process is gone and it is past
    /// <see cref="_StaleAfter"/>. Only roots that carry this run's marker file are ever removed.
    /// </summary>
    public static class TestDataDirectory
    {
        #region Public-Members

        /// <summary>
        /// Temp root this process was redirected to, or null when <see cref="Redirect"/> has not run.
        /// </summary>
        public static string? Root => _Root;

        /// <summary>
        /// The system temp directory as it was before <see cref="Redirect"/>, or null when it has not run.
        /// </summary>
        public static string? SystemTempRoot => _SystemTempRoot;

        /// <summary>
        /// Number of temp paths this process could not delete. Each one is also written to standard error.
        /// </summary>
        public static int CleanupFailures => _CleanupFailures;

        /// <summary>
        /// Prefix of every per-run temp root.
        /// </summary>
        public const string RootPrefix = "armada-test-home-";

        /// <summary>
        /// Environment variable that names the directory that holds the per-run roots, in place of the
        /// process temp directory.
        /// </summary>
        public const string TempRootVariable = "ARMADA_TEST_TEMP_ROOT";

        /// <summary>
        /// Marker file in every per-run temp root; it holds the owning process id.
        /// </summary>
        public const string MarkerFileName = ".armada-test-home";

        #endregion

        #region Private-Members

        private static string? _Root = null;
        private static string? _SystemTempRoot = null;
        private static int _CleanupFailures = 0;
        private static int _ExitHooked = 0;
        private static readonly TimeSpan _StaleAfter = TimeSpan.FromHours(2);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Point the default data directory at a fresh temp path for this process. Safe to call
        /// more than once; only the first call takes effect. Returns the temp root.
        /// </summary>
        public static string Redirect()
        {
            if (!String.IsNullOrEmpty(_Root)) return _Root!;

            // ARMADA_TEST_TEMP_ROOT names a faster temp directory than TMPDIR when the caller sends its own
            // build temp files elsewhere, as the server gate does with a memory-backed /tmp.
            string? tempRootOverride = Environment.GetEnvironmentVariable(TempRootVariable);
            string systemTemp = String.IsNullOrEmpty(tempRootOverride) ? Path.GetTempPath() : tempRootOverride!;
            SweepStaleRoots(systemTemp);

            string root = Path.Combine(systemTemp, RootPrefix + Guid.NewGuid().ToString("N"));
            string temp = Path.Combine(root, "tmp");

            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(root, MarkerFileName), Environment.ProcessId.ToString());

            // Path.GetTempPath reads these on every call, so every later temp path of this process and of
            // the processes it starts is under the root.
            Environment.SetEnvironmentVariable("TMPDIR", temp + Path.DirectorySeparatorChar);
            Environment.SetEnvironmentVariable("TMP", temp);
            Environment.SetEnvironmentVariable("TEMP", temp);

            // Referencing the variable NAME is a compile-time constant, so this does not trigger
            // static initialization of Constants and pin the real home before the redirect lands.
            Environment.SetEnvironmentVariable(Constants.DataDirectoryOverrideVariable, root);

            _SystemTempRoot = systemTemp;
            _Root = root;
            if (Interlocked.Exchange(ref _ExitHooked, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += (object? _, EventArgs __) => Cleanup();
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
        /// Remove the temp root and everything under it. Runs on process exit. A path that cannot be
        /// removed is counted and named on standard error; it never fails the run.
        /// </summary>
        public static void Cleanup()
        {
            if (String.IsNullOrEmpty(_Root)) return;
            TryDelete(_Root!);
        }

        /// <summary>
        /// Delete a temp file or directory tree. A failure is counted in <see cref="CleanupFailures"/> and
        /// named on standard error. Returns true when the path no longer exists.
        /// </summary>
        /// <param name="path">Path to delete.</param>
        public static bool TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Another process (a concurrent runner's sweep) can remove the same path first; a path that is
                // gone is deleted.
                if (!Directory.Exists(path) && !File.Exists(path)) return true;
                Interlocked.Increment(ref _CleanupFailures);
                Console.Error.WriteLine("TEST-TEMP: could not delete " + path + ": " + ex.Message);
                return false;
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Remove per-run roots that a killed run left behind: only roots with the marker file, whose
        /// owning process is gone, and that are older than <see cref="_StaleAfter"/>. A root from before the
        /// marker existed is never touched.
        /// </summary>
        /// <param name="systemTemp">Directory that holds the per-run roots.</param>
        public static void SweepStaleRoots(string systemTemp)
        {
            string[] roots;
            try
            {
                roots = Directory.GetDirectories(systemTemp, RootPrefix + "*");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("TEST-TEMP: could not list " + systemTemp + ": " + ex.Message);
                return;
            }

            DateTime cutoffUtc = DateTime.UtcNow - _StaleAfter;
            foreach (string root in roots)
            {
                string marker = Path.Combine(root, MarkerFileName);
                if (!File.Exists(marker)) continue;
                if (File.GetLastWriteTimeUtc(marker) > cutoffUtc) continue;
                if (Int32.TryParse(SafeRead(marker), out int pid) && IsProcessAlive(pid)) continue;
                TryDelete(root);
            }
        }

        private static string SafeRead(string path)
        {
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return String.Empty;
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        #endregion
    }
}
