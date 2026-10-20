namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// A bare repository holding three commits: a base, a stage commit that adds, modifies, deletes and renames
    /// paths (one with non-ASCII characters and a space, and one that differs from an unchanged path only in
    /// case), and a bulk commit that adds forty long-named files. The stage commit's index entry for the
    /// lower-case path is written with plumbing, so the fixture builds on a case-insensitive file system too.
    /// </summary>
    public sealed class CommitPairFixture : IDisposable
    {
        #region Public-Members

        /// <summary>Path of the stage commit's non-ASCII file.</summary>
        public const string UnicodePath = "docs/naïve café.md";

        /// <summary>Path that differs from the unchanged <c>Readme.md</c> only in case.</summary>
        public const string CaseDistinctPath = "readme.md";

        /// <summary>Number of files the bulk commit adds.</summary>
        public const int BulkFileCount = 40;

        /// <summary>Private root holding every fixture directory.</summary>
        public string Root { get; }

        /// <summary>The bare repository.</summary>
        public string BarePath { get; }

        /// <summary>Full id of the base commit.</summary>
        public string BaseCommit { get; private set; } = String.Empty;

        /// <summary>Full id of the stage commit.</summary>
        public string StageCommit { get; private set; } = String.Empty;

        /// <summary>Full id of the bulk commit, the tip of <c>main</c> in the bare repository.</summary>
        public string BulkCommit { get; private set; } = String.Empty;

        #endregion

        #region Constructors-and-Factories

        private CommitPairFixture(string root)
        {
            Root = root;
            BarePath = Path.Combine(root, "bare.git");
        }

        /// <summary>Build the fixture under a fresh temporary directory.</summary>
        /// <returns>The fixture.</returns>
        public static async Task<CommitPairFixture> CreateAsync()
        {
            CommitPairFixture fixture = new CommitPairFixture(
                Path.Combine(Path.GetTempPath(), "armada-commit-pair-" + Guid.NewGuid().ToString("N")));
            try
            {
                await fixture.BuildAsync().ConfigureAwait(false);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        #endregion

        #region Public-Methods

        /// <summary>Delete the fixture.</summary>
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            catch (Exception exception) { Console.WriteLine("Commit pair fixture cleanup failed: " + exception.Message); }
        }

        /// <summary>Run git in the bare repository and return trimmed output.</summary>
        /// <param name="arguments">Git arguments.</param>
        /// <returns>Trimmed standard output.</returns>
        public Task<string> RunInBareAsync(params string[] arguments)
        {
            return TestGit.RunAsync(BarePath, arguments);
        }

        /// <summary>
        /// The paths git itself reports between two commits with rename detection off, read NUL-separated.
        /// A rename appears as its old and its new path.
        /// </summary>
        /// <param name="fromCommit">Earlier commit.</param>
        /// <param name="toCommit">Later commit.</param>
        /// <returns>The set of changed paths.</returns>
        public async Task<HashSet<string>> GitChangedPathsAsync(string fromCommit, string toCommit)
        {
            string output = await RunInBareAsync("diff", "--name-only", "--no-renames", "-z", fromCommit, toCommit).ConfigureAwait(false);
            return new HashSet<string>(output.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        }

        #endregion

        #region Private-Methods

        private async Task BuildAsync()
        {
            string work = Path.Combine(Root, "work");
            Directory.CreateDirectory(work);
            await TestGit.RunAsync(work, "init", "--initial-branch=main").ConfigureAwait(false);
            await TestGit.RunAsync(work, "config", "core.ignorecase", "false").ConfigureAwait(false);

            WriteFile(work, "keep.txt", "keep version one\n");
            WriteFile(work, "remove.txt", "this file is removed by the stage commit and nothing else\n");
            WriteFile(work, "old/name.txt", String.Join("\n", RenamedContent()) + "\n");
            WriteFile(work, "Readme.md", "Upper-case readme that the stage leaves alone\n");
            await TestGit.RunAsync(work, "add", "-A").ConfigureAwait(false);
            await CommitAsync(work, "Base").ConfigureAwait(false);
            BaseCommit = await TestGit.RunAsync(work, "rev-parse", "HEAD").ConfigureAwait(false);

            WriteFile(work, "keep.txt", "keep version two\n");
            File.Delete(Path.Combine(work, "remove.txt"));
            Directory.CreateDirectory(Path.Combine(work, "new"));
            File.Move(Path.Combine(work, "old", "name.txt"), Path.Combine(work, "new", "name.txt"));
            WriteFile(work, UnicodePath, "content under a path with non-ASCII characters\n");
            await TestGit.RunAsync(work, "add", "-A").ConfigureAwait(false);
            string blobFile = Path.Combine(Root, "lower-readme.txt");
            File.WriteAllText(blobFile, "LOWER CASE README 0123456789 distinct body\n");
            string blob = await TestGit.RunAsync(work, "hash-object", "-w", blobFile).ConfigureAwait(false);
            await TestGit.RunAsync(work, "update-index", "--add", "--cacheinfo", "100644," + blob + "," + CaseDistinctPath).ConfigureAwait(false);
            await CommitAsync(work, "Stage").ConfigureAwait(false);
            StageCommit = await TestGit.RunAsync(work, "rev-parse", "HEAD").ConfigureAwait(false);

            for (int index = 0; index < BulkFileCount; index++)
                WriteFile(work, "bulk/a-file-with-a-long-descriptive-name-number-" + index.ToString("D2") + ".txt", "bulk " + index + "\n");
            await TestGit.RunAsync(work, "add", "bulk").ConfigureAwait(false);
            await CommitAsync(work, "Bulk").ConfigureAwait(false);
            BulkCommit = await TestGit.RunAsync(work, "rev-parse", "HEAD").ConfigureAwait(false);

            await TestGit.RunAsync(Root, "clone", "--bare", "--quiet", work, BarePath).ConfigureAwait(false);
        }

        private static IEnumerable<string> RenamedContent()
        {
            for (int line = 0; line < 20; line++) yield return "renamed content line " + line + " stays identical across the move";
        }

        private static void WriteFile(string root, string relativePath, string content)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private static Task<string> CommitAsync(string work, string message)
        {
            return TestGit.RunAsync(work, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message);
        }

        #endregion
    }
}
