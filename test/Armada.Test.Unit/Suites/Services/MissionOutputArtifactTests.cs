namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Regression coverage for durable, digest-backed mission output pages.
    /// </summary>
    public sealed class MissionOutputArtifactTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Output Artifact";

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Pages reconstruct complete Unicode output and preserve digest", () =>
            {
                string output = "first🙂secondéthird";
                Mission mission = BuildMission(output, MissionStatusEnum.Complete);
                StringBuilder rebuilt = new StringBuilder();
                int offset = 0;
                MissionOutputArtifactPage page;
                do
                {
                    page = MissionOutputArtifact.Build(mission, offset, 6);
                    rebuilt.Append(page.Content);
                    offset = page.NextOffset ?? page.TotalLength;
                }
                while (page.HasMore);

                string expectedDigest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
                AssertEqual(output, rebuilt.ToString(), "Paging must not split or replace Unicode data.");
                AssertEqual(expectedDigest, page.Sha256, "Every page must expose the digest for the full output.");
                AssertEqual(Encoding.UTF8.GetByteCount(output), page.TotalUtf8Bytes);
                AssertTrue(page.Finalized);
                AssertTrue(page.Complete);
                AssertNull(page.TruncationReason);
                return Task.CompletedTask;
            });

            await RunTest("Offset inside a surrogate pair is normalized without corrupting output", () =>
            {
                Mission mission = BuildMission("A🙂B", MissionStatusEnum.Complete);
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission, 2, 1);

                AssertEqual(1, page.Offset, "A low-surrogate offset must move to the rune boundary.");
                AssertEqual("🙂", page.Content);
                AssertEqual(3, page.NextOffset);
                return Task.CompletedTask;
            });

            await RunTest("Capture truncation is an explicit finalized evidence gap", () =>
            {
                Mission mission = BuildMission(
                    MissionOutputArtifact.StreamTruncationMarker + "\nretained tail",
                    MissionStatusEnum.Complete);
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission);

                AssertTrue(page.Finalized);
                AssertFalse(page.Complete);
                AssertEqual("stream_capture_limit", page.TruncationReason);
                return Task.CompletedTask;
            });

            await RunTest("Missing and live output state are explicit", () =>
            {
                Mission missing = BuildMission(null, MissionStatusEnum.Complete);
                MissionOutputArtifactPage missingPage = MissionOutputArtifact.Build(missing);
                AssertTrue(missingPage.Finalized);
                AssertFalse(missingPage.Complete);
                AssertEqual("output_unavailable", missingPage.TruncationReason);

                Mission live = BuildMission("partial", MissionStatusEnum.InProgress);
                MissionOutputArtifactPage livePage = MissionOutputArtifact.Build(live);
                AssertFalse(livePage.Finalized);
                AssertFalse(livePage.Complete);
                AssertEqual("mission_not_finalized", livePage.TruncationReason);
                return Task.CompletedTask;
            });

            await RunTest("Page requests are bounded and stable", () =>
            {
                Mission mission = BuildMission(new string('x', MissionOutputArtifact.MaximumPageLength + 5), MissionStatusEnum.Complete);
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission, 0, Int32.MaxValue);
                AssertEqual(0, page.Offset);
                AssertEqual(MissionOutputArtifact.MaximumPageLength, page.Length);
                AssertTrue(page.HasMore);
                AssertEqual(MissionOutputArtifact.MaximumPageLength, page.NextOffset);
                return Task.CompletedTask;
            });

            await RunTest("Artifact pages and digest use the same deterministic redacted text", () =>
            {
                Mission mission = BuildMission("token=testsecret\nresult ok", MissionStatusEnum.Complete);
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission);
                AssertFalse(page.Content.Contains("testsecret", StringComparison.Ordinal));
                AssertContains("token=[REDACTED]", page.Content);
                string digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(page.Content))).ToLowerInvariant();
                AssertEqual(digest, page.Sha256, "The digest must cover the exact safe artifact returned to callers.");
                return Task.CompletedTask;
            });

            await RunTest("Invalid page requests fail explicitly", () =>
            {
                Mission mission = BuildMission("short", MissionStatusEnum.Complete);
                bool negativeFailed = false;
                bool beyondFailed = false;
                try { MissionOutputArtifact.Build(mission, -1, 1); }
                catch (ArgumentOutOfRangeException) { negativeFailed = true; }
                try { MissionOutputArtifact.Build(mission, 99, 1); }
                catch (ArgumentOutOfRangeException) { beyondFailed = true; }
                AssertTrue(negativeFailed, "Negative offsets must not silently normalize.");
                AssertTrue(beyondFailed, "Offsets beyond the artifact must not look like a successful final page.");
                return Task.CompletedTask;
            });
        }

        private static Mission BuildMission(string? output, MissionStatusEnum status)
        {
            return new Mission("output artifact test")
            {
                Id = "mission-output-test",
                AgentOutput = output,
                Status = status
            };
        }
    }
}
