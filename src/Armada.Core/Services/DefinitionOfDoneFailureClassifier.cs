namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
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

        // Signatures that can only come from the environment: a restore ERROR, a missing SDK or
        // runtime, a shell that cannot find the command. These outrank test evidence, because a
        // dead environment also prints failed tests. A NuGet WARNING (NU1510 on every pruned
        // reference, for one) is not among them: read as a signature it labelled a single
        // deterministic assertion in an 8,000-test run as host trouble.
        private static readonly Regex _InfrastructurePattern = new Regex(
            @"(?:\brestore failed\b|\bfailed to restore\b|\bunable to load the service index\b|\berror\s+NU\d{4}\b|\bpackage\s+[^\r\n]+\s+not found\b|\bcould not resolve\b|\bcommand not found\b|\bis not recognized as an internal or external command\b|\bSDK\s+[^\r\n]+\s+not found\b|\bMSB4236\b|\bYou must install (?:or update )?\.NET to run this application\b|\bframework\s+'[^'\r\n]+',\s*version\s+'[^'\r\n]+'[^\r\n]*\bwas not found\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A test-run summary that reports zero failed tests ("Passed!  - Failed:     0, Passed: 12, ..."
        // or "Tests: 0 failed, 12 passed"). Such a line names the word "failed" but is evidence that the
        // run's tests passed, so it never counts as test-failure evidence.
        private static readonly Regex _ZeroFailureSummaryPattern = new Regex(
            @"(?:\bfailed:\s*0+\b|\b0+\s+failed\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A summary line also names a passing count, which tells a summary from a stray "Failed: 0".
        private static readonly Regex _PassedCountPattern = new Regex(
            @"(?:\bpassed:\s*\d+\b|\b\d+\s+passed\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A failure count above zero on a summary line ("Failed: 2", "3 failed").
        private static readonly Regex _NonZeroFailureCountPattern = new Regex(
            @"(?:\bfailed:\s*0*[1-9]\d*\b|\b0*[1-9]\d*\s+failed\b|\bfailures?:\s*0*[1-9]\d*\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string _StderrMarker = "--- STDERR ---";

        private static readonly Regex _RecordedClassPattern = new Regex(
            @"\Aclassification=(?<class>[A-Za-z]+)(?:;|\s|\z)",
            RegexOptions.Compiled);

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
            @"(?:\bOutOfProcNode\b|\bCleanupForBuild\b|\btest host process crashed\b|\bThe active test run was aborted\b|\bhost process exited\b|\btesthost process\b[^\r\n]*\bexited with error\b)",
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

        #region Public-Members

        /// <summary>
        /// Opening of the failure reason a mission records when its definition-of-done gate fails.
        /// The failure class name follows it, then a semicolon.
        /// </summary>
        public const string RecordedReasonPrefix = "DoD gate failed: ";

        /// <summary>
        /// Opening of the command label the gate records when a declared consumer's test suite fails
        /// against the change. The consumer vessel's name follows it. The gate verifies consumers only
        /// after the vessel's own build and tests pass, so this label means the failing tests are in
        /// the consumer's repository.
        /// </summary>
        public const string ConsumerTestsFailedLabelPrefix = "consumer_tests_failed: ";

        /// <summary>
        /// Opening of the command label the gate records when every test of a declared consumer's suite
        /// passed but its command failed in a step after the tests, on the run and on its one retry. The
        /// consumer vessel's name follows it. No consumer test failed, so this never reads as
        /// <see cref="ConsumerTestsFailedLabelPrefix"/>.
        /// </summary>
        public const string ConsumerPostTestStepFailedLabelPrefix = "consumer_post_test_step_failed: ";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the failure class a definition-of-done gate recorded in a mission failure reason.
        /// Only a reason that opens with the gate prefix counts, so a gate reason quoted inside
        /// another failure is not read as this mission's class.
        /// </summary>
        /// <param name="failureReason">Recorded mission failure reason.</param>
        /// <param name="failureClass">The recorded class when one is found.</param>
        /// <returns>True when the reason is a gate failure with a known class.</returns>
        public static bool TryReadRecordedClass(string? failureReason, out DefinitionOfDoneFailureClassEnum failureClass)
        {
            failureClass = DefinitionOfDoneFailureClassEnum.Infra;
            if (String.IsNullOrWhiteSpace(failureReason)) return false;

            string trimmed = failureReason.TrimStart();
            if (!trimmed.StartsWith(RecordedReasonPrefix, StringComparison.Ordinal)) return false;

            Match match = _RecordedClassPattern.Match(trimmed.Substring(RecordedReasonPrefix.Length));
            if (!match.Success) return false;

            string name = match.Groups["class"].Value;
            foreach (DefinitionOfDoneFailureClassEnum candidate in Enum.GetValues<DefinitionOfDoneFailureClassEnum>())
            {
                if (String.Equals(candidate.ToString(), name, StringComparison.Ordinal))
                {
                    failureClass = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Read the consumer vessel whose test suite failed a definition-of-done gate, from a recorded
        /// mission failure reason. Only a <see cref="DefinitionOfDoneFailureClassEnum.TestFail"/> gate
        /// reason whose command label opens with <see cref="ConsumerTestsFailedLabelPrefix"/> counts;
        /// the label is read from the reason's first line, so a consumer name printed in the output
        /// tail is never read as the failing command.
        /// </summary>
        /// <param name="failureReason">Recorded mission failure reason.</param>
        /// <param name="consumerName">The consumer vessel's name when one is found; otherwise empty.</param>
        /// <returns>True when the gate failed only on a consumer's test suite.</returns>
        public static bool TryReadConsumerTestFailure(string? failureReason, out string consumerName)
        {
            consumerName = String.Empty;
            if (!TryReadRecordedClass(failureReason, out DefinitionOfDoneFailureClassEnum failureClass)) return false;
            if (failureClass != DefinitionOfDoneFailureClassEnum.TestFail) return false;

            string trimmed = failureReason!.TrimStart();
            int newline = trimmed.IndexOfAny(new[] { '\r', '\n' });
            string firstLine = newline < 0 ? trimmed : trimmed.Substring(0, newline);
            int separator = firstLine.IndexOf(';');
            if (separator < 0) return false;

            string label = firstLine.Substring(separator + 1).TrimStart();
            if (!label.StartsWith(ConsumerTestsFailedLabelPrefix, StringComparison.Ordinal)) return false;

            string rest = label.Substring(ConsumerTestsFailedLabelPrefix.Length);
            int exited = rest.LastIndexOf(" command exited ", StringComparison.Ordinal);
            string name = (exited < 0 ? rest : rest.Substring(0, exited)).Trim();
            if (name.Length == 0) return false;

            consumerName = name;
            return true;
        }

        /// <summary>
        /// Classify a failed definition-of-done command. An explicit timeout takes
        /// precedence, followed by compiler, infrastructure, and test-failure evidence. A run whose
        /// test summaries all report zero failed tests, with no failing test named, is a
        /// <see cref="DefinitionOfDoneFailureClassEnum.PostTestStep"/> failure.
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
            if (HasTestFailureEvidence(combined))
                return DefinitionOfDoneFailureClassEnum.TestFail;

            // Every test run the command reported passed with zero failures, yet the command
            // exited non-zero: a step after the tests failed, not a test.
            if (CountZeroFailureSummaries(combined) > 0)
                return DefinitionOfDoneFailureClassEnum.PostTestStep;

            if (_WeakInfrastructurePattern.IsMatch(combined))
                return DefinitionOfDoneFailureClassEnum.Infra;

            if (IsTestCommand(commandLabel))
                return DefinitionOfDoneFailureClassEnum.TestFail;

            return DefinitionOfDoneFailureClassEnum.Infra;
        }

        /// <summary>
        /// Describe a <see cref="DefinitionOfDoneFailureClassEnum.PostTestStep"/> failure: how many test
        /// runs reported zero failed tests, how many tests passed, and the step that failed after them.
        /// The failing step is read from the last lines the command wrote to standard error, or, when it
        /// wrote none, from the last lines after its final test summary.
        /// </summary>
        /// <param name="output">Combined command output, with standard error after the stderr marker.</param>
        /// <param name="exitCode">Exit code the command reported.</param>
        /// <returns>One or two sentences an operator can act on.</returns>
        public static string DescribePostTestStepFailure(string? output, int exitCode)
        {
            string text = output ?? String.Empty;
            string[] lines = text.Split('\n');
            int summaries = 0;
            long passed = 0;
            int lastSummaryLine = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!IsZeroFailureSummaryLine(lines[i])) continue;
                summaries++;
                lastSummaryLine = i;
                Match count = Regex.Match(lines[i], @"\bpassed:\s*(?<n>\d+)\b|\b(?<n>\d+)\s+passed\b", RegexOptions.IgnoreCase);
                if (count.Success && Int64.TryParse(count.Groups["n"].Value, out long n)) passed += n;
            }

            List<string> stepLines = new List<string>();
            int stderrAt = text.LastIndexOf(_StderrMarker, StringComparison.Ordinal);
            if (stderrAt >= 0)
            {
                foreach (string line in text.Substring(stderrAt + _StderrMarker.Length).Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0) stepLines.Add(trimmed);
                }
            }
            else
            {
                for (int i = lastSummaryLine + 1; i < lines.Length; i++)
                {
                    string trimmed = lines[i].Trim();
                    if (trimmed.Length > 0) stepLines.Add(trimmed);
                }
            }

            const int maxStepLines = 3;
            if (stepLines.Count > maxStepLines) stepLines = stepLines.GetRange(stepLines.Count - maxStepLines, maxStepLines);

            string description = "0 tests failed: " + summaries + " test run(s) reported 0 failed tests ("
                + passed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " passed), but the command exited "
                + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " because a step after the tests failed.";
            if (stepLines.Count > 0)
                description += " The failing step reported: " + String.Join(" | ", stepLines);
            else
                description += " The failing step printed nothing.";
            return description;
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

        // Read line by line, so a zero-failure summary on one line cannot hide a failing test named on
        // another, and a summary that reports zero failures is never read as a failure.
        private static bool HasTestFailureEvidence(string output)
        {
            foreach (string line in output.Split('\n'))
            {
                if (!IsTestFailureDiagnosticLine(line)) continue;
                if (IsZeroFailureSummaryLine(line)) continue;
                return true;
            }
            return false;
        }

        private static int CountZeroFailureSummaries(string output)
        {
            int count = 0;
            foreach (string line in output.Split('\n'))
            {
                if (IsZeroFailureSummaryLine(line)) count++;
            }
            return count;
        }

        private static bool IsZeroFailureSummaryLine(string? line)
        {
            return !String.IsNullOrWhiteSpace(line)
                && _ZeroFailureSummaryPattern.IsMatch(line)
                && _PassedCountPattern.IsMatch(line)
                && !_NonZeroFailureCountPattern.IsMatch(line);
        }

        private static bool IsTestCommand(string? commandLabel)
        {
            return !String.IsNullOrWhiteSpace(commandLabel)
                && commandLabel.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion
    }
}
