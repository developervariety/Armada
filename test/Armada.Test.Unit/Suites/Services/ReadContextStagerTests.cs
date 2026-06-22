namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests for <see cref="ReadContextStager"/>: glob expansion, read-only staging,
    /// size guards, containment checks, and copier ReadOnly attribute behavior.
    /// </summary>
    public class ReadContextStagerTests : TestSuite
    {
        public override string Name => "ReadContextStager";

        protected override async Task RunTestsAsync()
        {
            // ---------------------------------------------------------------
            // Stager: basic glob staging
            // ---------------------------------------------------------------

            await RunTest("Stager_ExplicitPath_StagesFileReadOnlyUnderRefs", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    string src = WriteTempFile(root, "hello.txt", "content");
                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    List<ReadContextRequest> requests = new List<ReadContextRequest>
                    {
                        new ReadContextRequest(src)
                    };

                    StageResult result = stager.Stage(requests, root, settings);
                    AssertNull(result.Error, "Stage should succeed");
                    AssertEqual(1, result.Entries.Count);
                    AssertTrue(result.Entries[0].ReadOnly, "Entry must be read-only");
                    AssertTrue(result.Entries[0].DestPath.StartsWith("_refs/"), "DestPath must be under _refs/");
                    AssertTrue(result.Entries[0].DestPath.EndsWith("hello.txt"), "DestPath must end with file name");
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            await RunTest("Stager_Glob_StagesMatchedFilesUnderRefs", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    WriteTempFile(root, "a.txt", "aaa");
                    WriteTempFile(root, "b.txt", "bbb");
                    WriteTempFile(root, "c.log", "ccc");

                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    string glob = Path.Combine(root, "*.txt").Replace('\\', '/');
                    List<ReadContextRequest> requests = new List<ReadContextRequest>
                    {
                        new ReadContextRequest(glob)
                    };

                    StageResult result = stager.Stage(requests, root, settings);
                    AssertNull(result.Error, "Stage should succeed: " + result.Error);
                    AssertEqual(2, result.Entries.Count, "Should match exactly two .txt files");
                    foreach (PrestagedFile entry in result.Entries)
                    {
                        AssertTrue(entry.ReadOnly, "All glob entries must be read-only");
                        AssertTrue(entry.DestPath.StartsWith("_refs/"), "DestPath must start with _refs/");
                    }
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            await RunTest("Stager_DestSubPath_PrependsSubPathUnderRefs", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    WriteTempFile(root, "notes.txt", "data");

                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());
                    string src = Path.Combine(root, "notes.txt");

                    List<ReadContextRequest> requests = new List<ReadContextRequest>
                    {
                        new ReadContextRequest(src, "docs")
                    };

                    StageResult result = stager.Stage(requests, root, settings);
                    AssertNull(result.Error, "Stage should succeed");
                    AssertEqual(1, result.Entries.Count);
                    AssertTrue(result.Entries[0].DestPath.StartsWith("_refs/docs/"), "DestPath must include the DestSubPath prefix");
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            await RunTest("Stager_NullOrEmptyRequests_ReturnsSuccessWithNoEntries", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                ReadContextStager stager = new ReadContextStager(SilentLogging());
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    StageResult nullResult = stager.Stage(null, root, settings);
                    AssertNull(nullResult.Error, "Null requests should succeed");
                    AssertEqual(0, nullResult.Entries.Count);

                    StageResult emptyResult = stager.Stage(new List<ReadContextRequest>(), root, settings);
                    AssertNull(emptyResult.Error, "Empty requests should succeed");
                    AssertEqual(0, emptyResult.Entries.Count);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            // ---------------------------------------------------------------
            // Size guards: over per-file cap
            // ---------------------------------------------------------------

            await RunTest("Stager_OverPerFileBytesCap_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    // Write a file 1 byte over the minimum clamp of 1024 bytes.
                    // Setting MaxReadContextFileBytes to 1 clamps to 1024, so we need
                    // a file > 1024 bytes to trigger the guard.
                    string src = WriteTempFileBytes(root, "big.bin", 1025);

                    CodeIndexSettings settings = new CodeIndexSettings();
                    settings.MaxReadContextFileBytes = 1; // clamps to 1024

                    ReadContextStager stager = new ReadContextStager(SilentLogging());
                    List<ReadContextRequest> requests = new List<ReadContextRequest>
                    {
                        new ReadContextRequest(src)
                    };

                    StageResult result = stager.Stage(requests, root, settings);
                    AssertNotNull(result.Error, "Should return error for over-per-file cap");
                    AssertTrue(result.Entries.Count == 0, "No partial staging on guard breach");
                    AssertContains("MaxReadContextFileBytes", result.Error!);
                    AssertContains(src, result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            // ---------------------------------------------------------------
            // Size guards: over total bytes cap
            // ---------------------------------------------------------------

            await RunTest("Stager_OverTotalBytesCap_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    // Two 33 KB files; their total (66 KB) exceeds the minimum clamp of
                    // 64 KB for MaxReadContextTotalBytes. Setting to 0 clamps to 64*1024 = 65536.
                    string src1 = WriteTempFileBytes(root, "f1.bin", 33000);
                    string src2 = WriteTempFileBytes(root, "f2.bin", 33000);

                    CodeIndexSettings settings = new CodeIndexSettings();
                    settings.MaxReadContextFileBytes = 1024 * 1024; // per-file is generous
                    settings.MaxReadContextTotalBytes = 0;           // clamps to 65536 (64 KB)

                    ReadContextStager stager = new ReadContextStager(SilentLogging());
                    List<ReadContextRequest> requests = new List<ReadContextRequest>
                    {
                        new ReadContextRequest(src1),
                        new ReadContextRequest(src2)
                    };

                    StageResult result = stager.Stage(requests, root, settings);
                    AssertNotNull(result.Error, "Should return error when total bytes exceeded");
                    AssertTrue(result.Entries.Count == 0, "No partial staging on guard breach");
                    AssertContains("MaxReadContextTotalBytes", result.Error!);
                    AssertContains(src2, result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            // ---------------------------------------------------------------
            // Size guards: over file count cap
            // ---------------------------------------------------------------

            await RunTest("Stager_OverFileCountCap_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    string src1 = WriteTempFile(root, "g1.txt", "a");
                    string src2 = WriteTempFile(root, "g2.txt", "b");

                    CodeIndexSettings settings = new CodeIndexSettings();
                    settings.MaxReadContextFileCount = 1;

                    ReadContextStager stager = new ReadContextStager(SilentLogging());
                    List<ReadContextRequest> requests = new List<ReadContextRequest>
                    {
                        new ReadContextRequest(src1),
                        new ReadContextRequest(src2)
                    };

                    StageResult result = stager.Stage(requests, root, settings);
                    AssertNotNull(result.Error, "Should return error when file count exceeded");
                    AssertTrue(result.Entries.Count == 0, "No partial staging on guard breach");
                    AssertContains("MaxReadContextFileCount", result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            // ---------------------------------------------------------------
            // Unreadable / missing explicit path
            // ---------------------------------------------------------------

            await RunTest("Stager_MissingExplicitPath_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    string missing = Path.Combine(root, "does_not_exist_" + Guid.NewGuid().ToString("N") + ".txt");
                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    StageResult result = stager.Stage(new List<ReadContextRequest> { new ReadContextRequest(missing) }, root, settings);
                    AssertNotNull(result.Error, "Should return error for missing path");
                    AssertContains("does not exist", result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            // ---------------------------------------------------------------
            // Zero-match glob
            // ---------------------------------------------------------------

            await RunTest("Stager_ZeroMatchGlob_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    // No .xyz files exist
                    string glob = Path.Combine(root, "*.xyz").Replace('\\', '/');
                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    StageResult result = stager.Stage(new List<ReadContextRequest> { new ReadContextRequest(glob) }, root, settings);
                    AssertNotNull(result.Error, "Should return error for zero-match glob");
                    AssertContains("matched no files", result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            // ---------------------------------------------------------------
            // Path escape / containment
            // ---------------------------------------------------------------

            await RunTest("Stager_ExplicitPathOutsideRoot_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                string outsideRoot = NewTempDir("armada_rcs_outside_");
                try
                {
                    string outsideFile = WriteTempFile(outsideRoot, "escape.txt", "secret");

                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    StageResult result = stager.Stage(
                        new List<ReadContextRequest> { new ReadContextRequest(outsideFile) },
                        root,
                        settings);

                    AssertNotNull(result.Error, "Should reject source outside host root");
                    AssertContains("outside the allowed host root", result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                    TryDeleteDir(outsideRoot);
                }
            });

            // ---------------------------------------------------------------
            // Copier: ReadOnly=true sets the file attribute
            // ---------------------------------------------------------------

            await RunTest("Copier_ReadOnlyTrue_SetsReadOnlyFileAttribute", () =>
            {
                string src = WriteTempFileGlobal("copier-ro-test.txt", "data");
                string worktree = NewTempDir("armada_rcs_wt_");
                try
                {
                    PrestagedFileCopier copier = new PrestagedFileCopier(SilentLogging());
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(src, "dest.txt") { ReadOnly = true }
                    };

                    string? failure = copier.CopyAll(entries, worktree);
                    AssertNull(failure, "Copy should succeed");

                    string dest = Path.Combine(worktree, "dest.txt");
                    AssertTrue(File.Exists(dest), "Destination file must exist");
                    FileAttributes attrs = File.GetAttributes(dest);
                    AssertTrue((attrs & FileAttributes.ReadOnly) != 0, "Destination file must have ReadOnly attribute set");
                }
                finally
                {
                    TryDelete(src);
                    ClearReadOnlyAndDeleteDir(worktree);
                }
            });

            await RunTest("Copier_ReadOnlyFalse_LeavesFileWritable", () =>
            {
                string src = WriteTempFileGlobal("copier-rw-test.txt", "data");
                string worktree = NewTempDir("armada_rcs_wt_");
                try
                {
                    PrestagedFileCopier copier = new PrestagedFileCopier(SilentLogging());
                    List<PrestagedFile> entries = new List<PrestagedFile>
                    {
                        new PrestagedFile(src, "dest.txt") { ReadOnly = false }
                    };

                    string? failure = copier.CopyAll(entries, worktree);
                    AssertNull(failure, "Copy should succeed");

                    string dest = Path.Combine(worktree, "dest.txt");
                    AssertTrue(File.Exists(dest), "Destination file must exist");
                    FileAttributes attrs = File.GetAttributes(dest);
                    AssertFalse((attrs & FileAttributes.ReadOnly) != 0, "Destination file must NOT have ReadOnly attribute set");
                }
                finally
                {
                    TryDelete(src);
                    TryDeleteDir(worktree);
                }
            });

            await RunTest("Copier_ContentBased_ReadOnlyTrue_SetsReadOnlyFileAttribute", () =>
            {
                string worktree = NewTempDir("armada_rcs_wt_");
                try
                {
                    PrestagedFileCopier copier = new PrestagedFileCopier(SilentLogging());
                    PrestagedFile entry = PrestagedFile.FromContent("ref.md", "# ref content");
                    entry.ReadOnly = true;

                    string? failure = copier.CopyAll(new List<PrestagedFile> { entry }, worktree);
                    AssertNull(failure, "Content-based copy should succeed");

                    string dest = Path.Combine(worktree, "ref.md");
                    FileAttributes attrs = File.GetAttributes(dest);
                    AssertTrue((attrs & FileAttributes.ReadOnly) != 0, "Content-based file must have ReadOnly attribute when ReadOnly=true");
                }
                finally
                {
                    ClearReadOnlyAndDeleteDir(worktree);
                }
            });

            // ---------------------------------------------------------------
            // Settings: clamp boundaries
            // ---------------------------------------------------------------

            await RunTest("Settings_MaxReadContextFileBytes_ClampsToMin", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxReadContextFileBytes = 0;
                AssertEqual(1024L, settings.MaxReadContextFileBytes, "Below min should clamp to 1 KB");
            });

            await RunTest("Settings_MaxReadContextFileBytes_ClampsToMax", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxReadContextFileBytes = long.MaxValue;
                AssertEqual(1024L * 1024L * 8L, settings.MaxReadContextFileBytes, "Above max should clamp to 8 MB");
            });

            await RunTest("Settings_MaxReadContextTotalBytes_ClampsToMin", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxReadContextTotalBytes = 0;
                AssertEqual(64L * 1024L, settings.MaxReadContextTotalBytes, "Below min should clamp to 64 KB");
            });

            await RunTest("Settings_MaxReadContextTotalBytes_ClampsToMax", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxReadContextTotalBytes = long.MaxValue;
                AssertEqual(1024L * 1024L * 64L, settings.MaxReadContextTotalBytes, "Above max should clamp to 64 MB");
            });

            await RunTest("Settings_MaxReadContextFileCount_ClampsToMin", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxReadContextFileCount = 0;
                AssertEqual(1, settings.MaxReadContextFileCount, "Below min should clamp to 1");
            });

            await RunTest("Settings_MaxReadContextFileCount_ClampsToMax", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                settings.MaxReadContextFileCount = 99999;
                AssertEqual(2000, settings.MaxReadContextFileCount, "Above max should clamp to 2000");
            });

            // ---------------------------------------------------------------
            // Argument-guard / misconfiguration branches
            // ---------------------------------------------------------------

            await RunTest("Stager_Constructor_NullLogging_Throws", () =>
            {
                AssertThrows<ArgumentNullException>(() => new ReadContextStager(null!), "Null logging must be rejected");
            });

            await RunTest("Stager_EmptySourceGlob_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    StageResult result = stager.Stage(
                        new List<ReadContextRequest> { new ReadContextRequest("") },
                        root,
                        settings);

                    AssertNotNull(result.Error, "Empty SourceGlob must be rejected");
                    AssertEqual(0, result.Entries.Count, "No entries on empty SourceGlob");
                    AssertContains("SourceGlob", result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            await RunTest("Stager_WhitespaceSourceGlob_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    StageResult result = stager.Stage(
                        new List<ReadContextRequest> { new ReadContextRequest("   ") },
                        root,
                        settings);

                    AssertNotNull(result.Error, "Whitespace SourceGlob must be rejected");
                    AssertEqual(0, result.Entries.Count, "No entries on whitespace SourceGlob");
                    AssertContains("SourceGlob", result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            await RunTest("Stager_EmptyHostRoot_ReturnsActionableError", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();
                ReadContextStager stager = new ReadContextStager(SilentLogging());

                StageResult result = stager.Stage(
                    new List<ReadContextRequest> { new ReadContextRequest("anything.txt") },
                    "",
                    settings);

                AssertNotNull(result.Error, "Empty host root must be rejected");
                AssertEqual(0, result.Entries.Count, "No entries when host root is empty");
                AssertContains("hostRootForRelativePaths", result.Error!);
            });

            await RunTest("Stager_NullSettings_ReturnsActionableError", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    StageResult result = stager.Stage(
                        new List<ReadContextRequest> { new ReadContextRequest("anything.txt") },
                        root,
                        null!);

                    AssertNotNull(result.Error, "Null settings must be rejected (cannot enforce guards)");
                    AssertEqual(0, result.Entries.Count, "No entries when settings is null");
                    AssertContains("settings", result.Error!);
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            // ---------------------------------------------------------------
            // Recursive glob preserves nested relative dest paths under _refs/
            // ---------------------------------------------------------------

            await RunTest("Stager_RecursiveGlob_PreservesNestedRelativeDestPaths", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    WriteNestedTempFile(root, "src/inner/deep.cs", "// deep");
                    WriteNestedTempFile(root, "src/top.cs", "// top");
                    WriteNestedTempFile(root, "src/skip.txt", "ignore me");

                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    string glob = (root + "/**/*.cs").Replace('\\', '/');
                    StageResult result = stager.Stage(
                        new List<ReadContextRequest> { new ReadContextRequest(glob) },
                        root,
                        settings);

                    AssertNull(result.Error, "Recursive glob should succeed: " + result.Error);
                    AssertEqual(2, result.Entries.Count, "Should match exactly the two .cs files");

                    bool foundDeep = false;
                    bool foundTop = false;
                    foreach (PrestagedFile entry in result.Entries)
                    {
                        AssertTrue(entry.ReadOnly, "All recursive-glob entries must be read-only");
                        AssertTrue(entry.DestPath.StartsWith("_refs/"), "DestPath must be under _refs/");
                        AssertFalse(entry.DestPath.Contains(".."), "DestPath must never contain '..'");
                        AssertFalse(Path.IsPathRooted(entry.DestPath), "DestPath must be relative, never absolute");
                        if (entry.DestPath == "_refs/src/inner/deep.cs") foundDeep = true;
                        if (entry.DestPath == "_refs/src/top.cs") foundTop = true;
                    }
                    AssertTrue(foundDeep, "Nested subdirectory structure must be preserved (src/inner/deep.cs)");
                    AssertTrue(foundTop, "Top-level relative path must be preserved (src/top.cs)");
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });

            await RunTest("Stager_MultipleExplicitRequests_AccumulateAllEntries", () =>
            {
                string root = NewTempDir("armada_rcs_root_");
                try
                {
                    string a = WriteTempFile(root, "one.txt", "1");
                    string b = WriteTempFile(root, "two.txt", "22");

                    CodeIndexSettings settings = new CodeIndexSettings();
                    ReadContextStager stager = new ReadContextStager(SilentLogging());

                    StageResult result = stager.Stage(
                        new List<ReadContextRequest>
                        {
                            new ReadContextRequest(a),
                            new ReadContextRequest(b)
                        },
                        root,
                        settings);

                    AssertNull(result.Error, "Multiple explicit requests should succeed: " + result.Error);
                    AssertEqual(2, result.Entries.Count, "Both explicit requests must accumulate as entries");
                }
                finally
                {
                    TryDeleteDir(root);
                }
            });
        }

        #region Private-Methods

        private static string NewTempDir(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string WriteTempFile(string dir, string name, string content)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            return path;
        }

        private static string WriteTempFileBytes(string dir, string name, int byteCount)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, new byte[byteCount]);
            return path;
        }

        private static string WriteNestedTempFile(string root, string relativePath, string content)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string? dir = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            return path;
        }

        private static string WriteTempFileGlobal(string name, string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_rcs_" + Guid.NewGuid().ToString("N") + "_" + name);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                }
            }
            catch { }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private static void ClearReadOnlyAndDeleteDir(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        #endregion
    }
}
