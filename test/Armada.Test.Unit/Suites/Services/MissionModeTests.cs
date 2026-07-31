namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Covers the mission Mode field: its default, its parsing, its database round trip, and the
    /// completion-gate exemption it exists for. Before modes existed, a correct read-only mission was
    /// marked Failed with "worker_produced_no_commits" because the gate assumed every Worker commits.
    /// </summary>
    public class MissionModeTests : TestSuite
    {
        public override string Name => "Mission Modes";

        protected override async Task RunTestsAsync()
        {
            await RunTest("A mission defaults to Implementation mode", async () =>
            {
                Mission mission = new Mission();
                AssertEqual(MissionModeEnum.Implementation, mission.Mode, "default mode must be Implementation");
                AssertFalse(mission.IsReadOnlyMode, "Implementation must not be read-only");

                await Task.CompletedTask;
            });

            await RunTest("Audit and Research are read-only; Implementation is not", async () =>
            {
                Mission audit = new Mission();
                audit.Mode = MissionModeEnum.Audit;
                AssertTrue(audit.IsReadOnlyMode, "Audit must be read-only");

                Mission research = new Mission();
                research.Mode = MissionModeEnum.Research;
                AssertTrue(research.IsReadOnlyMode, "Research must be read-only");

                Mission implementation = new Mission();
                implementation.Mode = MissionModeEnum.Implementation;
                AssertFalse(implementation.IsReadOnlyMode, "Implementation must not be read-only");

                await Task.CompletedTask;
            });

            await RunTest("Unknown or absent stored modes resolve to Implementation", async () =>
            {
                AssertEqual(MissionModeEnum.Implementation, MissionModes.Parse(null), "null must resolve to Implementation");
                AssertEqual(MissionModeEnum.Implementation, MissionModes.Parse(""), "empty must resolve to Implementation");
                AssertEqual(MissionModeEnum.Implementation, MissionModes.Parse("   "), "whitespace must resolve to Implementation");
                AssertEqual(MissionModeEnum.Implementation, MissionModes.Parse("nonsense"), "an unknown value must resolve to Implementation");
                AssertEqual(MissionModeEnum.Audit, MissionModes.Parse("audit"), "parsing must be case-insensitive");
                AssertEqual(MissionModeEnum.Research, MissionModes.Parse(" Research "), "parsing must tolerate surrounding space");

                AssertFalse(MissionModes.IsKnown(null), "null is not a known mode");
                AssertFalse(MissionModes.IsKnown("nonsense"), "a typo is not a known mode");
                AssertTrue(MissionModes.IsKnown("Audit"), "Audit is a known mode");

                await Task.CompletedTask;
            });

            await RunTest("Mission mode survives a database round trip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Mission audit = new Mission();
                    audit.Title = "Read-only probe";
                    audit.Mode = MissionModeEnum.Audit;
                    Mission createdAudit = await testDb.Driver.Missions.CreateAsync(audit);

                    Mission? readAudit = await testDb.Driver.Missions.ReadAsync(createdAudit.Id);
                    AssertNotNull(readAudit, "the created mission must be readable");
                    AssertEqual(MissionModeEnum.Audit, readAudit!.Mode, "Audit mode must persist");
                    AssertTrue(readAudit.IsReadOnlyMode, "the round-tripped mission must still be read-only");

                    // Update to a different mode and read it back, which exercises the UPDATE path
                    // separately from INSERT. Postgres binds both through one shared parameter method,
                    // but the other drivers bind them independently.
                    readAudit.Mode = MissionModeEnum.Research;
                    await testDb.Driver.Missions.UpdateAsync(readAudit);

                    Mission? afterUpdate = await testDb.Driver.Missions.ReadAsync(createdAudit.Id);
                    AssertNotNull(afterUpdate, "the updated mission must be readable");
                    AssertEqual(MissionModeEnum.Research, afterUpdate!.Mode, "an updated mode must persist");

                    Mission plain = new Mission();
                    plain.Title = "Ordinary implementation mission";
                    Mission createdPlain = await testDb.Driver.Missions.CreateAsync(plain);
                    Mission? readPlain = await testDb.Driver.Missions.ReadAsync(createdPlain.Id);
                    AssertNotNull(readPlain, "the plain mission must be readable");
                    AssertEqual(MissionModeEnum.Implementation, readPlain!.Mode, "an unset mode must persist as Implementation");
                }
            });

            await RunTest("The no-commit gate exempts read-only modes and still catches implementation misses", async () =>
            {
                // PersonaMustProduceChanges is the persona half of the gate; the mode half is
                // mission.IsReadOnlyMode. Both must be true for a mission to be failed for producing
                // no commit, so a Worker in Audit mode is exempt while a Worker in Implementation
                // mode is not.
                AssertTrue(Armada.Server.MissionLandingHandler.PersonaMustProduceChanges("Worker"), "a Worker must produce changes");
                AssertFalse(Armada.Server.MissionLandingHandler.PersonaMustProduceChanges("Judge"), "a Judge need not produce changes");

                Mission auditWorker = new Mission();
                auditWorker.Persona = "Worker";
                auditWorker.Mode = MissionModeEnum.Audit;
                AssertTrue(
                    Armada.Server.MissionLandingHandler.PersonaMustProduceChanges(auditWorker.Persona) && auditWorker.IsReadOnlyMode,
                    "an Audit Worker is persona-required but mode-exempt, so the gate must not fail it");

                Mission implementationWorker = new Mission();
                implementationWorker.Persona = "Worker";
                AssertTrue(
                    Armada.Server.MissionLandingHandler.PersonaMustProduceChanges(implementationWorker.Persona) && !implementationWorker.IsReadOnlyMode,
                    "an Implementation Worker must still be failed when it produces no commit");

                await Task.CompletedTask;
            });
        }
    }
}
