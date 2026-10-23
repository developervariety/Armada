namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// In-process record of the commands a passing definition-of-done gate ran in full, so an automatic check
    /// of the same command on the same commit can name that gate instead of running the suite again.
    /// </summary>
    /// <remarks>
    /// A gate and the Build or UnitTest check armed for the same voyage run the same configured command, and
    /// on a large vessel each run is a full suite. A pass is recorded only when the whole gate passes, the
    /// command passed on its first run (a pass from a re-run of failing classes is not a full-suite pass), the
    /// worktree had no uncommitted tracked change, and its HEAD was the same before and after the command. A
    /// failed, cancelled or timed-out gate records nothing. The record lives in process memory, like the
    /// registry of running gates: after a restart a check runs its command.
    /// </remarks>
    public static class DefinitionOfDoneCommandPasses
    {
        #region Public-Members

        /// <summary>
        /// Most passes kept. The oldest pass is dropped first.
        /// </summary>
        public const int Capacity = 256;

        #endregion

        #region Private-Members

        private static readonly object _Lock = new object();
        private static readonly LinkedList<DefinitionOfDoneCommandPass> _Passes = new LinkedList<DefinitionOfDoneCommandPass>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record a command a passing gate ran in full.
        /// </summary>
        /// <param name="pass">The pass.</param>
        public static void Record(DefinitionOfDoneCommandPass pass)
        {
            if (pass == null) throw new ArgumentNullException(nameof(pass));
            if (String.IsNullOrWhiteSpace(pass.VesselId) || String.IsNullOrWhiteSpace(pass.CommitHash) || String.IsNullOrWhiteSpace(pass.Command)) return;

            lock (_Lock)
            {
                _Passes.AddFirst(pass);
                while (_Passes.Count > Capacity) _Passes.RemoveLast();
            }
        }

        /// <summary>
        /// Find the latest gate pass of the same command on the same commit of the same vessel, run with the
        /// same environment variables.
        /// </summary>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="commitHash">Full commit hash the check measures.</param>
        /// <param name="command">The configured command the check runs.</param>
        /// <param name="environment">Environment variables the check adds, or null for none.</param>
        /// <returns>The pass, or null when no gate passed that command there.</returns>
        public static DefinitionOfDoneCommandPass? Find(string? vesselId, string? commitHash, string? command, IReadOnlyDictionary<string, string>? environment)
        {
            if (String.IsNullOrWhiteSpace(vesselId) || String.IsNullOrWhiteSpace(commitHash) || String.IsNullOrWhiteSpace(command)) return null;
            string fingerprint = FingerprintEnvironment(environment);
            string wanted = command.Trim();

            lock (_Lock)
            {
                foreach (DefinitionOfDoneCommandPass pass in _Passes)
                {
                    if (!String.Equals(pass.VesselId, vesselId, StringComparison.Ordinal)) continue;
                    if (!String.Equals(pass.CommitHash, commitHash.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!String.Equals(pass.Command.Trim(), wanted, StringComparison.Ordinal)) continue;
                    if (!String.Equals(pass.EnvironmentFingerprint, fingerprint, StringComparison.Ordinal)) continue;
                    return pass;
                }
            }

            return null;
        }

        /// <summary>
        /// Describe a set of environment variables in a stable form, so two runs compare equal only when they
        /// set the same variables to the same values.
        /// </summary>
        /// <param name="environment">Environment variables, or null for none.</param>
        /// <returns>The fingerprint; empty for none.</returns>
        public static string FingerprintEnvironment(IReadOnlyDictionary<string, string>? environment)
        {
            if (environment == null || environment.Count == 0) return String.Empty;
            return String.Join("\n", environment
                .Where(pair => !String.IsNullOrWhiteSpace(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key.Length + ":" + pair.Key + (pair.Value ?? String.Empty).Length + ":" + (pair.Value ?? String.Empty)));
        }

        /// <summary>
        /// Forget every recorded pass.
        /// </summary>
        public static void Clear()
        {
            lock (_Lock)
            {
                _Passes.Clear();
            }
        }

        #endregion
    }
}
