namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests the production prior-art source against a real git repository: a hit on an unlanded
    /// branch carries a bounded excerpt of the file on that branch, so the reader sees the evidence.
    /// </summary>
    public class GitPriorArtSourceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Prior Art Source Repository Search";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("An unlanded branch hit carries a bounded excerpt read from that branch", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-prior-art-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(root);
                    await RunGitAsync(root, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(root, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(root, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    File.WriteAllText(Path.Combine(root, "README.md"), "initial\n");
                    await RunGitAsync(root, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(root, "commit", "-m", "Initial commit").ConfigureAwait(false);

                    await RunGitAsync(root, "checkout", "-b", "feature/decoder").ConfigureAwait(false);
                    List<string> lines = new List<string>();
                    for (int i = 1; i <= 200; i++) lines.Add("// filler line " + i);
                    lines[99] = "public sealed class FrameDecoderWidget { }";
                    File.WriteAllText(Path.Combine(root, "FrameDecoderWidget.cs"), String.Join("\n", lines) + "\n");
                    await RunGitAsync(root, "add", "FrameDecoderWidget.cs").ConfigureAwait(false);
                    await RunGitAsync(root, "commit", "-m", "Add decoder").ConfigureAwait(false);
                    await RunGitAsync(root, "checkout", "main").ConfigureAwait(false);

                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    GitService git = new GitService(logging);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        GitPriorArtSource source = new GitPriorArtSource(git, git, testDb.Driver, logging);
                        PriorArtSearchContext context = new PriorArtSearchContext
                        {
                            VesselId = "vsl_example",
                            RepoPath = root,
                            TargetRef = "main",
                            DefaultBranch = "main"
                        };

                        IReadOnlyList<PriorArtHit> hits = await source.SearchAsync(
                            context, PriorArtWhereEnum.UnlandedBranch, new List<string> { "FrameDecoderWidget" }, CancellationToken.None).ConfigureAwait(false);

                        PriorArtHit? hit = hits.FirstOrDefault(h => h.Location.StartsWith("FrameDecoderWidget.cs:", StringComparison.Ordinal));
                        AssertNotNull(hit, "the branch hit is found");
                        AssertEqual("feature/decoder", hit!.Ref, "the hit names its branch");
                        AssertContains("public sealed class FrameDecoderWidget", hit.Excerpt, "the excerpt holds the matched line read from the branch");
                        int excerptLines = hit.Excerpt.Split('\n').Length;
                        AssertTrue(excerptLines <= 40, "the excerpt is bounded to forty lines, was " + excerptLines);
                        AssertFalse(hit.Excerpt.Contains("filler line 1\n", StringComparison.Ordinal), "the excerpt is a window, not the whole file");
                    }
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr);
                return stdout;
            }
        }
    }
}
