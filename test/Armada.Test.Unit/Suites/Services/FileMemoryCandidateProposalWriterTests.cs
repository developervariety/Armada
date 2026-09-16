namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests for the file proposal writer: it writes under corpus/memory-candidates, returns null
    /// when no root is configured, and refuses a proposals folder that resolves under a loaded memory
    /// folder (shared/ or repos/).
    /// </summary>
    public class FileMemoryCandidateProposalWriterTests : TestSuite
    {
        public override string Name => "File Memory Candidate Proposal Writer (D18)";

        private static MemoryCandidateProposal Proposal()
        {
            return new MemoryCandidateProposal
            {
                GroupKey = "vsl_example|BriefContradiction|stale sibling",
                Title = "stale sibling not found",
                Detail = "the dock sibling predates the landing",
                Category = "BriefContradiction",
                Count = 9,
                DistinctCaptainCount = 4,
                Vessels = "one vessel",
                DurableLesson = 0.94,
                Scope = "shared"
            };
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Write_WritesUnderCorpusMemoryCandidates", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "aimem_" + Guid.NewGuid().ToString("N"));
                try
                {
                    FileMemoryCandidateProposalWriter writer = new FileMemoryCandidateProposalWriter(root, new LoggingModule());
                    string? path = await writer.WriteAsync(Proposal(), CancellationToken.None).ConfigureAwait(false);

                    AssertNotNull(path);
                    AssertContains(Path.Combine("corpus", "memory-candidates"), path!);
                    AssertTrue(File.Exists(path!), "the proposal file exists");

                    string content = await File.ReadAllTextAsync(path!).ConfigureAwait(false);
                    AssertContains("Memory candidate", content);
                    AssertContains("shared", content);
                    AssertContains("Distinct captains: 4", content);

                    // Never under a loaded memory folder.
                    AssertFalse(path!.Contains(Path.Combine(root, "shared"), StringComparison.Ordinal), "not under shared/");
                    AssertFalse(path!.Contains(Path.Combine(root, "repos"), StringComparison.Ordinal), "not under repos/");
                }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            });

            await RunTest("Write_NoRoot_ReturnsNull", async () =>
            {
                FileMemoryCandidateProposalWriter writer = new FileMemoryCandidateProposalWriter(null, new LoggingModule());
                string? path = await writer.WriteAsync(Proposal(), CancellationToken.None).ConfigureAwait(false);
                AssertNull(path, "no configured root means no write, recorded as an event only");
            });

            await RunTest("Write_RootUnderSharedFolder_Refused", async () =>
            {
                // A root that would place the proposals folder under a loaded memory folder is refused.
                string root = Path.Combine(Path.GetTempPath(), "aimem_" + Guid.NewGuid().ToString("N"), "shared");
                try
                {
                    FileMemoryCandidateProposalWriter writer = new FileMemoryCandidateProposalWriter(root, new LoggingModule());
                    string? path = await writer.WriteAsync(Proposal(), CancellationToken.None).ConfigureAwait(false);
                    AssertNull(path, "a proposals folder under shared/ is refused");
                }
                finally
                {
                    string? parent = Directory.GetParent(root)?.FullName;
                    if (parent != null && Directory.Exists(parent)) Directory.Delete(parent, true);
                }
            });

            await RunTest("Write_StableFileNameForSameGroupKey", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "aimem_" + Guid.NewGuid().ToString("N"));
                try
                {
                    FileMemoryCandidateProposalWriter writer = new FileMemoryCandidateProposalWriter(root, new LoggingModule());
                    string? first = await writer.WriteAsync(Proposal(), CancellationToken.None).ConfigureAwait(false);
                    string? second = await writer.WriteAsync(Proposal(), CancellationToken.None).ConfigureAwait(false);

                    AssertNotNull(first);
                    AssertEqual(first!, second!, "the same group key writes the same file, not a duplicate");
                }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            });
        }
    }
}
