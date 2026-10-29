namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Core.Database.Sqlite;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Judge author preference and the no-eligible-captain hold of the assignment pass.
    /// </summary>
    public sealed partial class MissionServiceRequestedCaptainRoutingTests
    {
        private async Task<Captain> CreateAnyPersonaCaptainAsync(SqliteDatabaseDriver db, string name, CaptainStateEnum state, string? preferredPersona = null)
        {
            Captain captain = new Captain(name);
            captain.State = state;
            captain.Tier = CaptainTierEnum.Premium;
            captain.PreferredPersona = preferredPersona;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private async Task<Mission> CreateJudgeAfterWorkerAsync(SqliteDatabaseDriver db, Vessel vessel, Captain workerAuthor, string? requestedCaptainId = null)
        {
            Mission worker = new Mission("stage one", "Write the change.");
            worker.VesselId = vessel.Id;
            worker.Persona = "Worker";
            worker.Status = MissionStatusEnum.Complete;
            worker.CaptainId = workerAuthor.Id;
            worker = await db.Missions.CreateAsync(worker).ConfigureAwait(false);

            Mission judge = new Mission("stage two", "Review the change.");
            judge.VesselId = vessel.Id;
            judge.Persona = "Judge";
            judge.Status = MissionStatusEnum.Pending;
            judge.DependsOnMissionId = worker.Id;
            judge.RequestedCaptainId = requestedCaptainId;
            return await db.Missions.CreateAsync(judge).ConfigureAwait(false);
        }

        private async Task<bool?> JudgeAssignedAuthorFlagAsync(SqliteDatabaseDriver db, string missionId)
        {
            List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(missionId, 100).ConfigureAwait(false);
            ArmadaEvent? recorded = events.FirstOrDefault(evt => String.Equals(evt.EventType, MissionService.JudgeAssignedEventType, StringComparison.Ordinal));
            if (recorded == null) return null;
            using (JsonDocument payload = JsonDocument.Parse(recorded.Payload!))
                return payload.RootElement.GetProperty("isChainAuthor").GetBoolean();
        }

        private async Task RunCaptainEligibilityTestsAsync()
        {
            await RunTest("TryAssign_Judge_PrefersACaptainThatWroteNoEarlierStage_AndRecordsIt", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    // Legacy Routing alone would take the author, whose preferred persona is Judge.
                    Captain author = await CreateAnyPersonaCaptainAsync(testDb.Driver, "author", CaptainStateEnum.Idle, "Judge").ConfigureAwait(false);
                    Captain independent = await CreateAnyPersonaCaptainAsync(testDb.Driver, "independent", CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission judge = await CreateJudgeAfterWorkerAsync(testDb.Driver, vessel, author).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(judge, vessel).ConfigureAwait(false), "The Judge is assigned");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertEqual(independent.Id, read!.CaptainId, "A captain that wrote no earlier stage takes the Judge");
                    AssertEqual(false, await JudgeAssignedAuthorFlagAsync(testDb.Driver, judge.Id).ConfigureAwait(false), "The assignment records that the Judge is not an author");
                }
            });

            await RunTest("TryAssign_Judge_FallsBackToTheAuthorWhenNoOtherCaptainQualifies", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain author = await CreateAnyPersonaCaptainAsync(testDb.Driver, "author", CaptainStateEnum.Idle).ConfigureAwait(false);
                    // Not admitted for a Judge, so the author is the only admitted captain.
                    Captain worker = new Captain("worker-only");
                    worker.State = CaptainStateEnum.Idle;
                    worker.Tier = CaptainTierEnum.Premium;
                    worker.AllowedPersonas = "[\"Worker\"]";
                    await testDb.Driver.Captains.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = await CreateJudgeAfterWorkerAsync(testDb.Driver, vessel, author).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(judge, vessel).ConfigureAwait(false), "The Judge does not wait for an independent captain");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertEqual(author.Id, read!.CaptainId, "The author takes the Judge when no other captain is admitted");
                    AssertEqual(true, await JudgeAssignedAuthorFlagAsync(testDb.Driver, judge.Id).ConfigureAwait(false), "The assignment records that the Judge is an author");
                }
            });

            await RunTest("TryAssign_Judge_RequestedAuthorCaptainWinsOverTheIndependentCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain author = await CreateAnyPersonaCaptainAsync(testDb.Driver, "author", CaptainStateEnum.Idle).ConfigureAwait(false);
                    await CreateAnyPersonaCaptainAsync(testDb.Driver, "independent", CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission judge = await CreateJudgeAfterWorkerAsync(testDb.Driver, vessel, author, author.Id).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(judge, vessel).ConfigureAwait(false), "The Judge is assigned");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertEqual(author.Id, read!.CaptainId, "A requested captain overrides the author preference");
                }
            });

            await RunTest("TryAssign_NoEligibleCaptain_HoldsWithPerCaptainReasons_AndReleasesWhenOneQualifies", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain workerOnly = await CreateCaptainAsync(testDb.Driver, "worker-only", CaptainStateEnum.Idle, CaptainTierEnum.Premium).ConfigureAwait(false);
                    Captain quarantined = await CreateAnyPersonaCaptainAsync(testDb.Driver, "quarantined", CaptainStateEnum.Idle).ConfigureAwait(false);
                    quarantined.QuarantineUntilUtc = DateTime.UtcNow.AddHours(1);
                    await testDb.Driver.Captains.UpdateAsync(quarantined).ConfigureAwait(false);
                    Captain busy = await CreateAnyPersonaCaptainAsync(testDb.Driver, "busy", CaptainStateEnum.Working).ConfigureAwait(false);

                    Mission judge = new Mission("review", "Review the change.");
                    judge.VesselId = vessel.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    AssertFalse(await service.TryAssignAsync(judge, vessel).ConfigureAwait(false), "No captain may take the Judge");
                    Mission? held = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertEqual(MissionAssignmentHold.NoEligibleCaptain, held!.AssignmentHoldReason, "The mission records the no_eligible_captain hold");
                    AssertEqual(MissionStatusEnum.Pending, held.Status, "The mission stays Pending");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(judge.Id, 100).ConfigureAwait(false);
                    List<ArmadaEvent> heldEvents = events.Where(evt => evt.EventType == MissionAssignmentHold.HeldEventType).ToList();
                    AssertEqual(1, heldEvents.Count, "The hold is recorded once");
                    NoEligibleCaptainPayload payload = JsonSerializer.Deserialize<NoEligibleCaptainPayload>(heldEvents[0].Payload!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    AssertEqual(LegacyCaptainSelector.ReasonPersonaNotAllowed, payload.Captains.First(item => item.CaptainId == workerOnly.Id).Reason, "The persona lock is named");
                    AssertEqual(CaptainAssignmentEligibility.ReasonQuarantined, payload.Captains.First(item => item.CaptainId == quarantined.Id).Reason, "The quarantine is named");
                    AssertEqual(CaptainAssignmentEligibility.ReasonNotIdle, payload.Captains.First(item => item.CaptainId == busy.Id).Reason, "A busy captain is named");

                    await service.TryAssignAsync(judge, vessel).ConfigureAwait(false);
                    events = await testDb.Driver.Events.EnumerateByMissionAsync(judge.Id, 100).ConfigureAwait(false);
                    AssertEqual(1, events.Count(evt => evt.EventType == MissionAssignmentHold.HeldEventType), "An unchanged hold writes no second event");

                    Mission readForApi = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    await MissionReadDerivations.ApplyAsync(testDb.Driver, readForApi).ConfigureAwait(false);
                    AssertEqual(3, readForApi.AssignmentHoldCaptains.Count, "The mission read lists the per-captain reasons");

                    Captain judgeCapable = await CreateAnyPersonaCaptainAsync(testDb.Driver, "judge-capable", CaptainStateEnum.Idle).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(judge, vessel).ConfigureAwait(false), "A qualifying captain takes the Judge");
                    Mission? assigned = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertEqual(judgeCapable.Id, assigned!.CaptainId, "The eligible captain is assigned");
                    AssertNull(assigned.AssignmentHoldReason, "The hold is released");
                    events = await testDb.Driver.Events.EnumerateByMissionAsync(judge.Id, 100).ConfigureAwait(false);
                    AssertEqual(1, events.Count(evt => evt.EventType == MissionAssignmentHold.ReleasedEventType), "The release is recorded");
                }
            });
        }
    }
}
