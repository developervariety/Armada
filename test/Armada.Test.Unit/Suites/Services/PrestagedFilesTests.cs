namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests for the prestagedFiles dispatch feature: validator rejection rules,
    /// the host-side copy executor, and database round-trip persistence.
    /// </summary>
    public class PrestagedFilesTests : TestSuite
    {
        public override string Name => "Prestaged Files";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Validator accepts an empty list", () =>
            {
                List<string> errors = PrestagedFileValidator.Validate(null);
                AssertEqual(0, errors.Count);
                errors = PrestagedFileValidator.Validate(new List<PrestagedFile>());
                AssertEqual(0, errors.Count);
            });

            await RunTest("Validator rejects relative source path", () =>
            {
                List<PrestagedFile> entries = new List<PrestagedFile>
                {
                    new PrestagedFile("relative/path.txt", "dest.txt")
                };
                List<string> errors = PrestagedFileValidator.Validate(entries);
                AssertEqual(1, errors.Count);
                AssertContains("must be an absolute path", errors[0]);
                AssertContains("relative/path.txt", errors[0]);
            });

            await RunTest("Validator rejects absolute dest path", () =>
            {
                string source = WriteTempFile("hello\n");
                try
                {
                    string absoluteDest = OperatingSystem.IsWindows() ? "C:/already-rooted.txt" : "/already-rooted.txt";
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(source, absoluteDest)
                    };
                    List<string> errors = PrestagedFileValidator.Validate(entries);
                    AssertEqual(1, errors.Count);
                    AssertContains("destPath must be relative", errors[0]);
                }
                finally
                {
                    TryDelete(source);
                }
            });

            await RunTest("Validator rejects '..' segment in dest path", () =>
            {
                string source = WriteTempFile("hello\n");
                try
                {
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(source, "subdir/../escape.txt")
                    };
                    List<string> errors = PrestagedFileValidator.Validate(entries);
                    AssertEqual(1, errors.Count);
                    AssertContains("'..'", errors[0]);
                    AssertContains("subdir/../escape.txt", errors[0]);
                }
                finally
                {
                    TryDelete(source);
                }
            });

            await RunTest("Validator rejects empty dest path", () =>
            {
                string source = WriteTempFile("hello\n");
                try
                {
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(source, "")
                    };
                    List<string> errors = PrestagedFileValidator.Validate(entries);
                    AssertEqual(1, errors.Count);
                    AssertContains("destPath is empty", errors[0]);
                }
                finally
                {
                    TryDelete(source);
                }
            });

            await RunTest("Validator rejects missing source file", () =>
            {
                string missing = Path.Combine(Path.GetTempPath(), "armada_missing_" + Guid.NewGuid().ToString("N") + ".txt");
                List<PrestagedFile> entries = new List<PrestagedFile>
                {
                    new PrestagedFile(missing, "x.txt")
                };
                List<string> errors = PrestagedFileValidator.Validate(entries);
                AssertEqual(1, errors.Count);
                AssertContains("does not exist", errors[0]);
                AssertContains(missing, errors[0]);
            });

            await RunTest("Validator rejects directory as source", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "armada_pf_dir_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(dir, "x.txt")
                    };
                    List<string> errors = PrestagedFileValidator.Validate(entries);
                    AssertEqual(1, errors.Count);
                    AssertContains("must be a file, not a directory", errors[0]);
                }
                finally
                {
                    TryDeleteDir(dir);
                }
            });

            await RunTest("Validator rejects too many entries", () =>
            {
                string source = WriteTempFile("a");
                try
                {
                    List<PrestagedFile> entries = new List<PrestagedFile>();
                    for (int i = 0; i <= PrestagedFileValidator.MaxEntriesPerMission; i++)
                    {
                        entries.Add(new PrestagedFile(source, "f" + i + ".txt"));
                    }
                    List<string> errors = PrestagedFileValidator.Validate(entries);
                    AssertEqual(1, errors.Count);
                    AssertContains("exceeds maximum", errors[0]);
                }
                finally
                {
                    TryDelete(source);
                }
            });

            await RunTest("Validator rejects oversized total bytes", () =>
            {
                // Write a single ~1 MB file and reference it many times so the total
                // crosses the 50 MB limit without burning 50 MB of disk per test.
                string source = Path.Combine(Path.GetTempPath(), "armada_pf_large_" + Guid.NewGuid().ToString("N") + ".bin");
                File.WriteAllBytes(source, new byte[1024 * 1024]); // 1 MB
                try
                {
                    List<PrestagedFile> entries = new List<PrestagedFile>();
                    for (int i = 0; i < 51; i++) entries.Add(new PrestagedFile(source, "copy" + i + ".bin"));
                    // 51 entries trips the count cap before the byte cap; cap entries at 50 here
                    // so we can isolate the byte-size rejection.
                    entries = entries.GetRange(0, PrestagedFileValidator.MaxEntriesPerMission);
                    // 50 entries x 1 MB = 50 MB which equals the cap, so add one more byte by
                    // appending a final small entry that pushes us over.
                    string big = Path.Combine(Path.GetTempPath(), "armada_pf_extra_" + Guid.NewGuid().ToString("N") + ".bin");
                    File.WriteAllBytes(big, new byte[16]);
                    try
                    {
                        // Drop one of the 1-MB entries and replace with the small one so we still have 50 entries
                        // but with 49 MB + 16 bytes -- not over. Instead use a larger source file.
                        // Simpler: write a 2 MB source and reference it 30 times = 60 MB.
                    }
                    finally { TryDelete(big); }

                    string source2 = Path.Combine(Path.GetTempPath(), "armada_pf_2mb_" + Guid.NewGuid().ToString("N") + ".bin");
                    File.WriteAllBytes(source2, new byte[2 * 1024 * 1024]); // 2 MB
                    try
                    {
                        List<PrestagedFile> over = new List<PrestagedFile>();
                        for (int i = 0; i < 30; i++) over.Add(new PrestagedFile(source2, "copy" + i + ".bin"));
                        List<string> errors = PrestagedFileValidator.Validate(over);
                        AssertEqual(1, errors.Count);
                        AssertContains("total bytes exceeds maximum", errors[0]);
                    }
                    finally
                    {
                        TryDelete(source2);
                    }
                }
                finally
                {
                    TryDelete(source);
                }
            });

            await RunTest("Copier round-trips a single file into the worktree", () =>
            {
                string source = WriteTempFile("hello prestaged\n");
                string worktree = NewTempDir("armada_pf_wt_");
                try
                {
                    PrestagedFileCopier copier = new PrestagedFileCopier(SilentLogging());
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(source, "subdir/dest.txt")
                    };

                    string? failure = copier.CopyAll(entries, worktree);
                    AssertNull(failure, "Copy should succeed");

                    string copied = Path.Combine(worktree, "subdir", "dest.txt");
                    AssertTrue(File.Exists(copied), "Destination file should exist");
                    AssertEqual("hello prestaged\n", File.ReadAllText(copied).Replace("\r\n", "\n"));
                }
                finally
                {
                    TryDelete(source);
                    TryDeleteDir(worktree);
                }
            });

            await RunTest("Copier fails cleanly when destPath already exists", () =>
            {
                string source = WriteTempFile("new content\n");
                string worktree = NewTempDir("armada_pf_wt_");
                try
                {
                    string existing = Path.Combine(worktree, "already.txt");
                    File.WriteAllText(existing, "pre-existing\n");

                    PrestagedFileCopier copier = new PrestagedFileCopier(SilentLogging());
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(source, "already.txt")
                    };

                    string? failure = copier.CopyAll(entries, worktree);
                    AssertNotNull(failure, "Copy should fail when destPath exists");
                    AssertContains("already exists", failure!);

                    // Pre-existing content untouched.
                    AssertEqual("pre-existing\n", File.ReadAllText(existing).Replace("\r\n", "\n"));
                }
                finally
                {
                    TryDelete(source);
                    TryDeleteDir(worktree);
                }
            });

            await RunTest("Copier rejects destPath that resolves outside worktree", () =>
            {
                // Validation should already block this, but the copier has its own
                // defense-in-depth check on the resolved absolute path. Use a
                // ".." segment that the validator misses by skipping the call,
                // which is what would happen if a later code path appended a bad
                // PrestagedFile after validation.
                string source = WriteTempFile("escape attempt\n");
                string worktree = NewTempDir("armada_pf_wt_");
                try
                {
                    PrestagedFileCopier copier = new PrestagedFileCopier(SilentLogging());
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(source, "..\\escape.txt")
                    };

                    string? failure = copier.CopyAll(entries, worktree);
                    AssertNotNull(failure, "Copy should fail when destPath escapes worktree");
                    AssertContains("outside the worktree root", failure!);
                }
                finally
                {
                    TryDelete(source);
                    TryDeleteDir(worktree);
                }
            });

            await RunTest("Mission round-trips PrestagedFiles through SQLite", async () =>
            {
                using (TestHelpers.TestDatabase db = await TestHelpers.TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("title", "desc");
                    mission.PrestagedFiles = new List<PrestagedFile>
                    {
                        new PrestagedFile("/abs/source.txt", "rel/dest.txt"),
                        new PrestagedFile("/abs/other.bin", "fixture.bin")
                    };
                    Mission created = await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    AssertEqual(2, created.PrestagedFiles!.Count);

                    Mission? read = await db.Driver.Missions.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertNotNull(read!.PrestagedFiles);
                    AssertEqual(2, read.PrestagedFiles!.Count);
                    AssertEqual("/abs/source.txt", read.PrestagedFiles[0].SourcePath);
                    AssertEqual("rel/dest.txt", read.PrestagedFiles[0].DestPath);
                    AssertEqual("fixture.bin", read.PrestagedFiles[1].DestPath);
                }
            });

            await RunTest("Mission stores null PrestagedFiles when not supplied", async () =>
            {
                using (TestHelpers.TestDatabase db = await TestHelpers.TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("title", "desc");
                    Mission created = await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? read = await db.Driver.Missions.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertNull(read!.PrestagedFiles, "PrestagedFiles should round-trip as null when not set");
                }
            });
        }

        private static string WriteTempFile(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_pf_src_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, content);
            return path;
        }

        private static string NewTempDir(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }
    }
}
