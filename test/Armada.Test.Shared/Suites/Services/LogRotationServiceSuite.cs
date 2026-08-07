namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using SyslogLogging;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the <see cref="LogRotationService"/>: single-file rotation under and over the
    /// configured size threshold, shifting of existing numbered files, and safe no-op handling for
    /// nonexistent/null/empty paths. Also covers directory-wide rotation of large files and a no-op
    /// on a missing directory. Each case creates and cleans up an isolated temp directory.
    /// </summary>
    public sealed class LogRotationServiceSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.LogRotationService";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Log Rotation Service suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("rotate_if_needed_under_threshold_no_rotation", "RotateIfNeeded UnderThreshold NoRotation", TestTags.Positive, () =>
            {
                LogRotationTestContext context = CreateTestContext();
                string testDir = context.TestDir;
                LogRotationService service = context.Service;
                try
                {
                    string filePath = Path.Combine(testDir, "small.log");
                    File.WriteAllText(filePath, "short");

                    service.RotateIfNeeded(filePath);

                    AssertTrue(File.Exists(filePath));
                    AssertFalse(File.Exists(filePath + ".1"));
                }
                finally
                {
                    CleanupTestDir(testDir);
                }
            }));

            cases.Add(Case("rotate_if_needed_over_threshold_rotates_file", "RotateIfNeeded OverThreshold RotatesFile", TestTags.Positive, () =>
            {
                LogRotationTestContext context = CreateTestContext();
                string testDir = context.TestDir;
                LogRotationService service = context.Service;
                try
                {
                    string filePath = Path.Combine(testDir, "large.log");
                    File.WriteAllText(filePath, new string('x', 200));

                    service.RotateIfNeeded(filePath);

                    AssertFalse(File.Exists(filePath));
                    AssertTrue(File.Exists(filePath + ".1"));
                }
                finally
                {
                    CleanupTestDir(testDir);
                }
            }));

            cases.Add(Case("rotate_if_needed_shifts_existing_files", "RotateIfNeeded ShiftsExistingFiles", TestTags.Positive, () =>
            {
                LogRotationTestContext context = CreateTestContext();
                string testDir = context.TestDir;
                LogRotationService service = context.Service;
                try
                {
                    string filePath = Path.Combine(testDir, "shift.log");

                    File.WriteAllText(filePath + ".1", "old-1");
                    File.WriteAllText(filePath + ".2", "old-2");

                    File.WriteAllText(filePath, new string('x', 200));

                    service.RotateIfNeeded(filePath);

                    AssertTrue(File.Exists(filePath + ".1"));
                    AssertTrue(File.Exists(filePath + ".2"));
                    AssertTrue(File.Exists(filePath + ".3"));
                    AssertEqual("old-1", File.ReadAllText(filePath + ".2"));
                }
                finally
                {
                    CleanupTestDir(testDir);
                }
            }));

            cases.Add(Case("rotate_if_needed_non_existent_file_no_op", "RotateIfNeeded NonExistentFile NoOp", TestTags.Negative, () =>
            {
                LogRotationTestContext context = CreateTestContext();
                string testDir = context.TestDir;
                LogRotationService service = context.Service;
                try
                {
                    service.RotateIfNeeded(Path.Combine(testDir, "nonexistent.log"));
                }
                finally
                {
                    CleanupTestDir(testDir);
                }
            }));

            cases.Add(Case("rotate_if_needed_null_path_no_op", "RotateIfNeeded NullPath NoOp", TestTags.Negative, () =>
            {
                LogRotationTestContext context = CreateTestContext();
                string testDir = context.TestDir;
                LogRotationService service = context.Service;
                try
                {
                    service.RotateIfNeeded(null!);
                }
                finally
                {
                    CleanupTestDir(testDir);
                }
            }));

            cases.Add(Case("rotate_if_needed_empty_path_no_op", "RotateIfNeeded EmptyPath NoOp", TestTags.Negative, () =>
            {
                LogRotationTestContext context = CreateTestContext();
                string testDir = context.TestDir;
                LogRotationService service = context.Service;
                try
                {
                    service.RotateIfNeeded("");
                }
                finally
                {
                    CleanupTestDir(testDir);
                }
            }));

            cases.Add(Case("rotate_all_in_directory_rotates_large_files", "RotateAllInDirectory RotatesLargeFiles", TestTags.Positive, () =>
            {
                LogRotationTestContext context = CreateTestContext();
                string testDir = context.TestDir;
                LogRotationService service = context.Service;
                try
                {
                    File.WriteAllText(Path.Combine(testDir, "a.log"), new string('x', 200));
                    File.WriteAllText(Path.Combine(testDir, "b.log"), "small");

                    service.RotateAllInDirectory(testDir);

                    AssertTrue(File.Exists(Path.Combine(testDir, "a.log.1")));
                    AssertTrue(File.Exists(Path.Combine(testDir, "b.log")));
                    AssertFalse(File.Exists(Path.Combine(testDir, "b.log.1")));
                }
                finally
                {
                    CleanupTestDir(testDir);
                }
            }));

            cases.Add(Case("rotate_all_in_directory_non_existent_dir_no_op", "RotateAllInDirectory NonExistentDir NoOp", TestTags.Negative, () =>
            {
                LogRotationTestContext context = CreateTestContext();
                string testDir = context.TestDir;
                LogRotationService service = context.Service;
                try
                {
                    service.RotateAllInDirectory("/nonexistent/path");
                }
                finally
                {
                    CleanupTestDir(testDir);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Log Rotation Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static LogRotationTestContext CreateTestContext()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "armada_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            LogRotationService service = new LogRotationService(logging, 100, 3); // 100 bytes max, 3 files max
            return new LogRotationTestContext(testDir, service);
        }

        private static void CleanupTestDir(string testDir)
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, true);
            }
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
