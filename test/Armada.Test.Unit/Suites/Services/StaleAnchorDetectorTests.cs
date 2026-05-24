namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Tests for StaleAnchorDetector: missing-file and no-graph graceful behavior.</summary>
    public class StaleAnchorDetectorTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Stale Anchor Detector";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Detect_NoReflectionEvents_ReturnsEmptyWarnings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-empty", localPath: null).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    AssertEqual(0, result.CheckedEventCount, "no events checked");
                    AssertEqual(0, result.Warnings.Count, "no warnings when no events");
                    AssertEqual(vessel.Id, result.VesselId, "vessel ID preserved in result");
                }
            });

            await RunTest("Detect_NoLocalPath_FileChecksSkipped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-nolocalpath", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        vessel.Id,
                        mission.Id,
                        filePaths: new List<string> { "src/Missing/File.cs" },
                        sourceMissionIds: new List<string> { mission.Id }).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    AssertFalse(result.FileChecksAvailable, "file checks unavailable without local path");
                    AssertEqual("no_local_path", result.SkipReason, "skip reason set");
                    int missingFileWarnings = CountWarnings(result.Warnings, "missing_file");
                    AssertEqual(0, missingFileWarnings, "no missing_file warnings without local path");
                }
            });

            await RunTest("Detect_WithLocalPath_MissingFileFlagged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada-stale-test-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    try
                    {
                        Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-missingfile", localPath: tempDir).ConfigureAwait(false);
                        Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                        await CreateAcceptedEventAsync(
                            testDb.Driver,
                            vessel.Id,
                            mission.Id,
                            filePaths: new List<string> { "src/Definitely/NotHere.cs" },
                            sourceMissionIds: new List<string> { mission.Id }).ConfigureAwait(false);

                        StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                            testDb.Driver, vessel.Id).ConfigureAwait(false);

                        AssertTrue(result.FileChecksAvailable, "file checks available with local path");
                        AssertNull(result.SkipReason, "no skip reason when local path present");
                        int missingFileWarnings = CountWarnings(result.Warnings, "missing_file");
                        AssertTrue(missingFileWarnings > 0, "missing_file warning produced for absent file");
                        AssertTrue(
                            result.Warnings.Exists(w => w.AffectedPath == "src/Definitely/NotHere.cs"),
                            "affected path matches anchor file path");
                    }
                    finally
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
            });

            await RunTest("Detect_WithLocalPath_ExistingFileNotFlagged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada-stale-exist-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    try
                    {
                        string relPath = "src/Present.cs";
                        string absPath = Path.Combine(tempDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
                        File.WriteAllText(absPath, "// exists");

                        Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-exists", localPath: tempDir).ConfigureAwait(false);
                        Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                        await CreateAcceptedEventAsync(
                            testDb.Driver,
                            vessel.Id,
                            mission.Id,
                            filePaths: new List<string> { relPath },
                            sourceMissionIds: new List<string> { mission.Id }).ConfigureAwait(false);

                        StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                            testDb.Driver, vessel.Id).ConfigureAwait(false);

                        int missingFileWarnings = CountWarnings(result.Warnings, "missing_file");
                        AssertEqual(0, missingFileWarnings, "no missing_file warning for existing file");
                    }
                    finally
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
            });

            await RunTest("Detect_MissingSourceMission_FlagsWarning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-missmission", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    string phantomMissionId = "msn_phantom_doesnotexist99";
                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        vessel.Id,
                        mission.Id,
                        filePaths: new List<string>(),
                        sourceMissionIds: new List<string> { phantomMissionId }).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    int missingMissionWarnings = CountWarnings(result.Warnings, "missing_mission");
                    AssertTrue(missingMissionWarnings > 0, "missing_mission warning produced for absent mission ID");
                    AssertTrue(
                        result.Warnings.Exists(w => w.AffectedMissionId == phantomMissionId),
                        "affected mission ID matches phantom");
                }
            });

            await RunTest("Detect_PresentSourceMission_NotFlagged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-pressmission", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);
                    await CreateAcceptedEventAsync(
                        testDb.Driver,
                        vessel.Id,
                        mission.Id,
                        filePaths: new List<string>(),
                        sourceMissionIds: new List<string> { mission.Id }).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    int missingMissionWarnings = CountWarnings(result.Warnings, "missing_mission");
                    AssertEqual(0, missingMissionWarnings, "present mission not flagged as stale");
                }
            });

            await RunTest("Detect_EventWithNullPayload_GracefullySkipped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-nullpayload", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);

                    ArmadaEvent ev = new ArmadaEvent("reflection.accepted", "test null payload");
                    ev.VesselId = vessel.Id;
                    ev.MissionId = mission.Id;
                    ev.Payload = null;
                    await testDb.Driver.Events.CreateAsync(ev).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    AssertEqual(1, result.CheckedEventCount, "null-payload event still counted as checked");
                    AssertEqual(0, result.Warnings.Count, "no warnings from null-payload event");
                }
            });

            await RunTest("Detect_NonReflectionEvents_NotCounted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "stale-nonrefl", localPath: null).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel.Id).ConfigureAwait(false);

                    ArmadaEvent ev = new ArmadaEvent("merge_queue.enqueued", "unrelated event");
                    ev.VesselId = vessel.Id;
                    ev.MissionId = mission.Id;
                    await testDb.Driver.Events.CreateAsync(ev).ConfigureAwait(false);

                    StaleAnchorDetectionResult result = await StaleAnchorDetector.DetectAsync(
                        testDb.Driver, vessel.Id).ConfigureAwait(false);

                    AssertEqual(0, result.CheckedEventCount, "non-reflection event not counted");
                    AssertEqual(0, result.Warnings.Count, "no warnings from non-reflection event");
                }
            });
        }

        private static async Task<Vessel> CreateVesselAsync(DatabaseDriver database, string name, string? localPath)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git");
            vessel.TenantId = Constants.DefaultTenantId;
            vessel.LocalPath = localPath;
            return await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateMissionAsync(DatabaseDriver database, string vesselId)
        {
            Mission mission = new Mission("stale-detector-test", "stale anchor test");
            mission.VesselId = vesselId;
            mission.Persona = "MemoryConsolidator";
            mission.Status = MissionStatusEnum.WorkProduced;
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task CreateAcceptedEventAsync(
            DatabaseDriver database,
            string vesselId,
            string missionId,
            List<string> filePaths,
            List<string> sourceMissionIds)
        {
            ArmadaEvent ev = new ArmadaEvent("reflection.accepted", "stale detector test event");
            ev.VesselId = vesselId;
            ev.MissionId = missionId;
            ev.Payload = JsonSerializer.Serialize(new
            {
                playbookId = "art_stale_test",
                missionId = missionId,
                vesselId = vesselId,
                anchors = new
                {
                    sourceMissionIds = sourceMissionIds,
                    filePaths = filePaths,
                    confidence = "high",
                    evidenceKind = "verbatim"
                }
            });
            await database.Events.CreateAsync(ev).ConfigureAwait(false);
        }

        private static int CountWarnings(List<StaleAnchorWarning> warnings, string warnKind)
        {
            int count = 0;
            foreach (StaleAnchorWarning w in warnings)
            {
                if (w.WarnKind == warnKind)
                    count++;
            }

            return count;
        }
    }
}
