namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>Tests bounded, mission-bound diagnostic capture storage and byte paging.</summary>
    public sealed class MissionLogCaptureArtifactTests : TestSuite
    {
        public override string Name => "Mission Log Capture Artifact";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Byte pages reconstruct redacted UTF-8 output with stable hash and long-line support", async () =>
            {
                string root = CreateRoot();
                try
                {
                    string missionId = "msn_capture_001";
                    string output = new string('a', 15998) + "🙂" + new string('b', 70000) + "\nlast";
                    MissionLogCaptureMetadata capture = await MissionLogCaptureArtifact.WriteAsync(root, missionId, output).ConfigureAwait(false);
                    StringBuilder rebuilt = new StringBuilder();
                    long offset = 0;
                    MissionLogCapturePage page;
                    do
                    {
                        page = await MissionLogCaptureArtifact.ReadPageAsync(root, missionId, capture.CaptureId, offset, 16000, capture.Sha256).ConfigureAwait(false);
                        AssertNull(page.Error, "A valid page has no error");
                        AssertTrue(page.Content.Length > 0 || !page.HasMore, "Each non-final page makes progress");
                        AssertTrue(page.LengthBytes <= 16000, "Pages stay within the requested byte limit");
                        rebuilt.Append(page.Content);
                        offset = page.OffsetBytes + page.LengthBytes;
                    }
                    while (page.HasMore);

                    string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
                    AssertEqual(output, rebuilt.ToString(), "Pages must preserve a single long line and multibyte characters without loss");
                    AssertEqual(expectedHash, capture.Sha256);
                    AssertEqual(expectedHash, page.Sha256);
                    AssertEqual(Encoding.UTF8.GetByteCount(output), capture.TotalUtf8Bytes);
                    AssertTrue(page.Complete, "A runner-complete capture is complete");
                }
                finally { TryDelete(root); }
            });

            await RunTest("Runner truncation is separate from capture paging completeness", async () =>
            {
                string root = CreateRoot();
                try
                {
                    MissionLogCaptureMetadata capture = await MissionLogCaptureArtifact.WriteAsync(
                        root, "msn_capture_002", "retained tail", runnerOutputTruncated: true, runnerOutputOmittedBytes: 27).ConfigureAwait(false);
                    MissionLogCapturePage page = await MissionLogCaptureArtifact.ReadPageAsync(
                        root, "msn_capture_002", capture.CaptureId, 0, 16000, capture.Sha256).ConfigureAwait(false);
                    AssertFalse(page.Complete, "A sound file cannot claim the runner output was complete when it dropped bytes");
                    AssertTrue(page.RunnerOutputTruncated);
                    AssertEqual(27L, page.RunnerOutputOmittedBytes);
                }
                finally { TryDelete(root); }
            });

            await RunTest("Runner drain anomalies remain incomplete even when no byte cap was hit", async () =>
            {
                string root = CreateRoot();
                try
                {
                    MissionLogCaptureMetadata capture = await MissionLogCaptureArtifact.WriteAsync(
                        root, "msn_capture_drain", "received output", runnerOutputIncomplete: true).ConfigureAwait(false);
                    MissionLogCapturePage page = await MissionLogCaptureArtifact.ReadPageAsync(
                        root, "msn_capture_drain", capture.CaptureId, 0, 16000, capture.Sha256).ConfigureAwait(false);
                    AssertFalse(page.Complete, "A drain anomaly cannot claim complete process output");
                    AssertTrue(page.RunnerOutputIncomplete);
                    AssertFalse(page.RunnerOutputTruncated, "Drain state is separate from byte-cap truncation");
                }
                finally { TryDelete(root); }
            });

            await RunTest("Capture identity is mission-bound and path values cannot traverse", async () =>
            {
                string root = CreateRoot();
                try
                {
                    MissionLogCaptureMetadata capture = await MissionLogCaptureArtifact.WriteAsync(root, "msn_capture_003", "safe").ConfigureAwait(false);
                    MissionLogCapturePage crossMission = await MissionLogCaptureArtifact.ReadPageAsync(root, "msn_capture_004", capture.CaptureId, 0, 10).ConfigureAwait(false);
                    AssertEqual("capture not found", crossMission.Error);
                    bool badCaptureRejected = false;
                    bool badMissionRejected = false;
                    try { await MissionLogCaptureArtifact.ReadPageAsync(root, "msn_capture_003", "../../etc/passwd", 0, 10).ConfigureAwait(false); }
                    catch (ArgumentException) { badCaptureRejected = true; }
                    try { await MissionLogCaptureArtifact.ReadPageAsync(root, "msn_../escape", capture.CaptureId, 0, 10).ConfigureAwait(false); }
                    catch (ArgumentException) { badMissionRejected = true; }
                    bool oversizedPageRejected = false;
                    try { await MissionLogCaptureArtifact.ReadPageAsync(root, "msn_capture_003", capture.CaptureId, 0, MissionLogCaptureArtifact.MaximumPageLengthBytes + 1).ConfigureAwait(false); }
                    catch (ArgumentOutOfRangeException) { oversizedPageRejected = true; }
                    AssertTrue(badCaptureRejected, "Capture IDs must not permit traversal");
                    AssertTrue(badMissionRejected, "Mission IDs must not permit traversal");
                    AssertTrue(oversizedPageRejected, "Capture page reads must enforce the server bound");
                }
                finally { TryDelete(root); }
            });

            await RunTest("Capture redacts secret text and normal mission log resolution ignores capture sidecars", async () =>
            {
                string root = CreateRoot();
                try
                {
                    string missionId = "msn_capture_005";
                    MissionLogCaptureMetadata capture = await MissionLogCaptureArtifact.WriteAsync(
                        root, missionId, "token=hidden-secret\nline").ConfigureAwait(false);
                    MissionLogCapturePage page = await MissionLogCaptureArtifact.ReadPageAsync(root, missionId, capture.CaptureId, 0, 1000, capture.Sha256).ConfigureAwait(false);
                    AssertContains("token=[REDACTED]", page.Content);
                    AssertFalse(page.Content.Contains("hidden-secret", StringComparison.Ordinal));

                    string missionLog = Path.Combine(root, "missions", missionId + ".log");
                    await File.WriteAllTextAsync(missionLog, "ordinary session log").ConfigureAwait(false);
                    AssertEqual(missionLog, SessionLogReader.ResolveMissionLogPath(root, missionId), "The capture sidecar must not replace the ordinary log");
                }
                finally { TryDelete(root); }
            });

            await RunTest("Capture reads reject invalid metadata and report the stable digest for caller verification", async () =>
            {
                string root = CreateRoot();
                try
                {
                    string missionId = "msn_capture_006";
                    MissionLogCaptureMetadata capture = await MissionLogCaptureArtifact.WriteAsync(root, missionId, "verified body").ConfigureAwait(false);
                    MissionLogCapturePage wrongDigest = await MissionLogCaptureArtifact.ReadPageAsync(
                        root, missionId, capture.CaptureId, 0, 32, new string('0', 64)).ConfigureAwait(false);
                    AssertContains("hash does not match", wrongDigest.Error ?? String.Empty);

                    string path = Path.Combine(root, "missions", missionId + ".dod-" + capture.CaptureId + ".log");
                    byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    int bodyOffset = Array.IndexOf(bytes, (byte)'\n') + 1;
                    bytes[bodyOffset] = (byte)'X';
                    await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                    MissionLogCapturePage damaged = await MissionLogCaptureArtifact.ReadPageAsync(
                        root, missionId, capture.CaptureId, 0, 32, capture.Sha256).ConfigureAwait(false);
                    AssertNull(damaged.Error, "A bounded page remains readable when a body byte changes");
                    string pageDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(damaged.Content))).ToLowerInvariant();
                    AssertFalse(String.Equals(pageDigest, damaged.Sha256, StringComparison.Ordinal),
                        "Reassembled-page verification detects body corruption against the stable whole-capture digest");
                }
                finally { TryDelete(root); }
            });
        }

        private static string CreateRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "armada-mission-capture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void TryDelete(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
