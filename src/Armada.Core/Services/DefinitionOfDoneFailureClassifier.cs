namespace Armada.Core.Services
{
    using System;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;

    /// <summary>
    /// Deterministically classifies definition-of-done command failures from generic
    /// command labels, exit signals, and common compiler, test, restore, or setup output.
    /// </summary>
    public sealed class DefinitionOfDoneFailureClassifier
    {
        #region Private-Members

        private static readonly Regex _CompilerDiagnosticPattern = new Regex(
            @"(?:\berror\s+(?:CS|BC|FS|TS|C|LNK)\d+\b|:\s*(?:fatal\s+)?error(?:\s+[A-Z]+\d+)?\s*:|\berror\[[A-Z]\d+\]|\bcompilation failed\b|\bcompiler error\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _TestFailurePattern = new Regex(
            @"(?:\btests?\s+failed\b|\btest run failed\b|\bfailed:\s*\d+\b|\b\d+\s+failed\b|\bfailures?:\s*\d+\b|\[FAIL\]|\bFAIL(?:ED)?\b.*\b(?:test|assert))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Signatures that can only come from the environment: a restore ERROR, a missing SDK, a
        // shell that cannot find the command. These outrank test evidence, because a dead
        // environment also prints failed tests. A NuGet WARNING (NU1510 on every pruned
        // reference, for one) is not among them: read as a signature it labelled a single
        // deterministic assertion in an 8,000-test run as host trouble.
        private static readonly Regex _InfrastructurePattern = new Regex(
            @"(?:\brestore failed\b|\bfailed to restore\b|\bunable to load the service index\b|\berror\s+NU\d{4}\b|\bpackage\s+[^\r\n]+\s+not found\b|\bcould not resolve\b|\bcommand not found\b|\bis not recognized as an internal or external command\b|\bSDK\s+[^\r\n]+\s+not found\b|\bMSB4236\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Words that a healthy test run can print too -- a test named for dependency injection, a
        // fixture that probes an optional file, a build step that continues on error. They point
        // at the environment only when no test evidence explains the failure; read before the
        // test evidence they labelled a single deterministic assertion as host trouble.
        private static readonly Regex _WeakInfrastructurePattern = new Regex(
            @"(?:\bdependency\b|\bno such file or directory\b|\bpermission denied\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A test-host crash: many failures land at once with a process-level signature, and the
        // failed tests are victims rather than evidence.
        private static readonly Regex _TestHostCrashPattern = new Regex(
            @"(?:\bOutOfProcNode\b|\bCleanupForBuild\b|\btest host process crashed\b|\bThe active test run was aborted\b|\bhost process exited\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Container-runtime unavailability. A dock without a working Docker/Podman daemon fails
        /// every container-backed fixture, and the runner reports that as ordinary test failures
        /// ("Failed: 12"), so the mission looks like broken code when the environment is simply
        /// missing. These signals must outrank test-failure evidence.
        /// </summary>
        private static readonly Regex _ContainerRuntimePattern = new Regex(
            @"(?:cannot connect to the docker daemon|error during connect[^\r\n]*docker|docker daemon is not running|is the docker daemon running|docker[^\r\n]{0,40}not running|the system cannot find the file specified[^\r\n]{0,40}docker|open //\./pipe/docker_engine|/var/run/docker\.sock|podman[^\r\n]{0,40}(?:not running|cannot connect)|testcontainers[^\r\n]{0,60}(?:could not|unable|failed to connect)|docker api responded with status code=5\d\d)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Classify a failed definition-of-done command. An explicit timeout takes
        /// precedence, followed by compiler, infrastructure, and test-failure evidence.
        /// </summary>
        /// <param name="commandLabel">Generic label for the command that failed.</param>
        /// <param name="exitCode">Exit code reported by the command.</param>
        /// <param name="output">Combined standard output and standard error.</param>
        /// <param name="timedOut">Whether the command exceeded its configured timeout.</param>
        /// <returns>The structured failure classification.</returns>
        public DefinitionOfDoneFailureClassEnum Classify(
            string? commandLabel,
            int exitCode,
            string? output,
            bool timedOut = false)
        {
            if (timedOut)
                return DefinitionOfDoneFailureClassEnum.Timeout;

            if (exitCode < 0)
                return DefinitionOfDoneFailureClassEnum.Infra;

            string combined = output ?? String.Empty;
            // A crashed test host prints a compiler-shaped MSBuild error on the way down, so it is
            // read before the compile branch; a genuine compile failure carries no crash signature.
            if (_TestHostCrashPattern.IsMatch(combined))
                return DefinitionOfDoneFailureClassEnum.Infra;

            if (IsCompilerDiagnosticLine(combined))
                return DefinitionOfDoneFailureClassEnum.Compile;

            // Checked before the test-failure branch on purpose: a dead container runtime usually
            // ALSO prints "Failed: N" for every container-backed fixture, so matching on test
            // evidence first would blame the code for a missing environment.
            if (_ContainerRuntimePattern.IsMatch(combined))
                return DefinitionOfDoneFailureClassEnum.Infra;

            if (_InfrastructurePattern.IsMatch(combined))
                return DefinitionOfDoneFailureClassEnum.Infra;

            // Test evidence outranks a weak environment word: a run that names its failed tests
            // failed on those tests, whatever else it printed on the way.
            if (IsTestFailureDiagnosticLine(combined))
                return DefinitionOfDoneFailureClassEnum.TestFail;

            if (_WeakInfrastructurePattern.IsMatch(combined))
                return DefinitionOfDoneFailureClassEnum.Infra;

            if (IsTestCommand(commandLabel))
                return DefinitionOfDoneFailureClassEnum.TestFail;

            return DefinitionOfDoneFailureClassEnum.Infra;
        }

        #endregion

        #region Internal-Methods

        internal static bool IsActionableDiagnosticLine(string? line)
        {
            return IsCompilerDiagnosticLine(line) || IsTestFailureDiagnosticLine(line);
        }

        #endregion

        #region Private-Methods

        private static bool IsCompilerDiagnosticLine(string? line)
        {
            return !String.IsNullOrWhiteSpace(line) && _CompilerDiagnosticPattern.IsMatch(line);
        }

        private static bool IsTestFailureDiagnosticLine(string? line)
        {
            return !String.IsNullOrWhiteSpace(line) && _TestFailurePattern.IsMatch(line);
        }

        private static bool IsTestCommand(string? commandLabel)
        {
            return !String.IsNullOrWhiteSpace(commandLabel)
                && commandLabel.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion
    }
}
