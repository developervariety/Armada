namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the captain-log screening cadence host: what it reads, what it records, what it
    /// posts, and the mission state it must leave untouched.
    /// </summary>
    public sealed class CaptainLogScreenServiceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Log Screen Service";

        #region Test-Doubles

        private sealed class FakeTailReader : ICaptainLogTailReader
        {
            public Dictionary<string, string?> TailsByMissionId { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);
            public int Reads { get; private set; }

            public Task<string?> ReadTailAsync(Mission mission, int lines, CancellationToken token)
            {
                Reads++;
                return Task.FromResult(TailsByMissionId.TryGetValue(mission.Id, out string? tail) ? tail : null);
            }
        }

        private sealed class RecordingPass : ICaptainLogScreenPass
        {
            private readonly List<LogScreenFinding> _Findings;

            public RecordingPass(params LogScreenFinding[] findings)
            {
                _Findings = findings.ToList();
            }

            public string Name => "recording";
            public int Calls { get; private set; }
            public List<LogScreenContext> Contexts { get; } = new List<LogScreenContext>();

            public Task<IReadOnlyList<LogScreenFinding>> EvaluateAsync(LogScreenContext context, CancellationToken token)
            {
                Calls++;
                Contexts.Add(context);
                return Task.FromResult<IReadOnlyList<LogScreenFinding>>(_Findings);
            }
        }

        private sealed class ThrowingPass : ICaptainLogScreenPass
        {
            public string Name => "throwing";

            public Task<IReadOnlyList<LogScreenFinding>> EvaluateAsync(LogScreenContext context, CancellationToken token)
            {
                throw new InvalidOperationException("pass refused to evaluate");
            }
        }

        private sealed class RecordingNotePoster : IVoyageTaggedNotePoster
        {
            public List<string> Contents { get; } = new List<string>();
            public List<string?> VoyageIds { get; } = new List<string?>();

            public Task PostVoyageNoteAsync(string content, string? voyageId, string? missionId, string? vesselId, CancellationToken token)
            {
                Contents.Add(content);
                VoyageIds.Add(voyageId);
                return Task.CompletedTask;
            }
        }

        #endregion

        #region Helpers

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static CaptainLogScreeningSettings EnabledSettings()
        {
            return new CaptainLogScreeningSettings
            {
                Enabled = true,
                IntervalSeconds = 30,
                TailLines = 100,
                CooldownMinutes = 30
            };
        }

        private static LogScreenFinding Finding(string ruleClass, string evidence)
        {
            return new LogScreenFinding
            {
                RuleClass = ruleClass,
                EvidenceLine = evidence,
                Source = LogScreenFinding.SourceRule
            };
        }

        private static async Task<Mission> SeedInProgressMissionAsync(TestDatabase db, string voyageName)
        {
            Voyage voyage = new Voyage(voyageName);
            voyage.Status = VoyageStatusEnum.InProgress;
            voyage = await db.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Vessel vessel = new Vessel("example-vessel-" + voyageName, "https://example.invalid/repo.git");
            vessel = await db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain("example-captain-" + voyageName);
            captain = await db.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

            Mission mission = new Mission("stage under screen", "description");
            mission.VoyageId = voyage.Id;
            mission.VesselId = vessel.Id;
            mission.CaptainId = captain.Id;
            mission.Status = MissionStatusEnum.InProgress;
            mission.StartedUtc = DateTime.UtcNow.AddMinutes(-20);
            return await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<List<ArmadaEvent>> ScreenEventsAsync(TestDatabase db)
        {
            return await db.Driver.Events
                .EnumerateByTypeAsync(CaptainLogScreenService.ScreenEventType, 100).ConfigureAwait(false);
        }

        private static readonly JsonSerializerOptions _PayloadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static CaptainLogScreenEventPayload PayloadOf(ArmadaEvent evt)
        {
            return JsonSerializer.Deserialize<CaptainLogScreenEventPayload>(evt.Payload!, _PayloadOptions)!;
        }

        #endregion

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Sweep_ScreeningDisabled_ReadsNoLogAndNamesTheReason", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                RecordingPass pass = new RecordingPass();

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, new CaptainLogScreeningSettings(), reader,
                    new List<ICaptainLogScreenPass> { pass });

                CaptainLogScreenSweepResult result = await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(CaptainLogScreenSweepOutcomeEnum.Disabled, result.Outcome);
                AssertNotNull(result.Reason);
                AssertEqual(0, reader.Reads);
                AssertEqual(0, pass.Calls);
                AssertEqual(0, (await ScreenEventsAsync(db).ConfigureAwait(false)).Count);
            });

            await RunTest("Sweep_NoInProgressMissions_HasItsOwnOutcomeValue", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), new FakeTailReader(),
                    new List<ICaptainLogScreenPass> { new RecordingPass() });

                CaptainLogScreenSweepResult result = await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(CaptainLogScreenSweepOutcomeEnum.NoActiveMissions, result.Outcome);
                AssertNotNull(result.Reason);
            });

            await RunTest("Sweep_CleanTail_RecordsTheScreenAndPostsNoNote", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "nothing of interest here";
                RecordingNotePoster poster = new RecordingNotePoster();

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { new RecordingPass() }, poster);

                CaptainLogScreenSweepResult result = await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, result.Screened);
                AssertEqual(1, result.Clean);
                AssertEqual(0, poster.Contents.Count);

                List<ArmadaEvent> events = await ScreenEventsAsync(db).ConfigureAwait(false);
                AssertEqual(1, events.Count);
                AssertEqual(CaptainLogScreenService.OutcomeClean, PayloadOf(events[0]).Outcome);
                AssertEqual(0, PayloadOf(events[0]).Counts.Count);
            });

            await RunTest("Sweep_Findings_PostsExactlyOneVoyageTaggedNoteAndOneEvent", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "a tail that trips two classes";
                RecordingNotePoster poster = new RecordingNotePoster();
                RecordingPass pass = new RecordingPass(
                    Finding("rule_one", "the first line of evidence"),
                    Finding("rule_one", "a second line for the same class"),
                    Finding("rule_two", "the other line of evidence"));

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { pass }, poster);

                CaptainLogScreenSweepResult result = await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, result.Flagged);
                AssertEqual(1, poster.Contents.Count);
                AssertEqual(mission.VoyageId, poster.VoyageIds[0]);
                AssertContains("rule_one", poster.Contents[0]);
                AssertContains("rule_two", poster.Contents[0]);
                AssertContains("the first line of evidence", poster.Contents[0]);
                AssertContains("the other line of evidence", poster.Contents[0]);

                List<ArmadaEvent> events = await ScreenEventsAsync(db).ConfigureAwait(false);
                AssertEqual(1, events.Count);
                AssertEqual(CaptainLogScreenService.OutcomeFlagged, PayloadOf(events[0]).Outcome);
                Dictionary<string, int> counts = PayloadOf(events[0]).Counts;
                AssertEqual(2, counts["rule_one"]);
                AssertEqual(1, counts["rule_two"]);
                AssertEqual(mission.VoyageId, events[0].VoyageId);
            });

            await RunTest("Sweep_UnchangedTail_IssuesNoPassCallAndNoRecord", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "a tail that does not move";
                RecordingPass pass = new RecordingPass();
                DateTime clock = DateTime.UtcNow;

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { pass }, null, () => clock);

                await sut.SweepAsync().ConfigureAwait(false);
                AssertEqual(1, pass.Calls);

                clock = clock.AddHours(1);
                CaptainLogScreenSweepResult second = await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, pass.Calls);
                AssertEqual(1, second.UnchangedTails);
                AssertEqual(0, second.Screened);
                AssertEqual(1, (await ScreenEventsAsync(db).ConfigureAwait(false)).Count);
            });

            await RunTest("Sweep_ChangedTail_IsScreenedAgain", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "first content";
                RecordingPass pass = new RecordingPass();
                DateTime clock = DateTime.UtcNow;

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { pass }, null, () => clock);

                await sut.SweepAsync().ConfigureAwait(false);
                reader.TailsByMissionId[mission.Id] = "first content plus a new line";
                clock = clock.AddHours(1);
                await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(2, pass.Calls);
                AssertEqual(2, (await ScreenEventsAsync(db).ConfigureAwait(false)).Count);
            });

            await RunTest("Sweep_FlaggedMissionInCooldown_PostsNoSecondNote", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "a tail with a finding";
                RecordingNotePoster poster = new RecordingNotePoster();
                DateTime clock = DateTime.UtcNow;

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { new RecordingPass(Finding("rule_one", "evidence")) },
                    poster, () => clock);

                await sut.SweepAsync().ConfigureAwait(false);
                AssertEqual(1, poster.Contents.Count);

                // A later sweep, still inside the cooldown window, over a tail that has also moved on.
                reader.TailsByMissionId[mission.Id] = "a tail with a finding and one more line";
                clock = clock.AddMinutes(5);
                CaptainLogScreenSweepResult second = await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, poster.Contents.Count);
                AssertEqual(1, second.InCooldown);

                // Past the cooldown window the same mission can be flagged again.
                clock = clock.AddMinutes(60);
                await sut.SweepAsync().ConfigureAwait(false);
                AssertEqual(2, poster.Contents.Count);
            });

            await RunTest("Sweep_IntervalNotElapsed_ReadsNothingAndNamesTheReason", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "content";
                DateTime clock = DateTime.UtcNow;

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { new RecordingPass() }, null, () => clock);

                await sut.SweepAsync().ConfigureAwait(false);
                int readsAfterFirst = reader.Reads;

                CaptainLogScreenSweepResult second = await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(CaptainLogScreenSweepOutcomeEnum.IntervalNotElapsed, second.Outcome);
                AssertNotNull(second.Reason);
                AssertEqual(readsAfterFirst, reader.Reads);
            });

            await RunTest("Sweep_OneMissionThrows_TheOthersAreStillScreened", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission failing = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                Mission healthy = await SeedInProgressMissionAsync(db, "voyage-b").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[failing.Id] = "tail that makes the pass throw";
                reader.TailsByMissionId[healthy.Id] = "tail that screens cleanly";

                // The throwing pass runs for every mission; the healthy mission reaches its record
                // only because the failing mission's exception did not stop the sweep.
                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { new ConditionalThrowPass(failing.Id) });

                CaptainLogScreenSweepResult result = await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(CaptainLogScreenSweepOutcomeEnum.Completed, result.Outcome);
                AssertEqual(1, result.Errors);
                AssertEqual(1, result.Screened);
                List<ArmadaEvent> events = await ScreenEventsAsync(db).ConfigureAwait(false);
                AssertEqual(1, events.Count);
                AssertEqual(healthy.Id, events[0].MissionId);
            });

            await RunTest("Sweep_AllPassesThrow_SweepStillReturnsAndNeverThrows", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "content";

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { new ThrowingPass() });

                CaptainLogScreenSweepResult result = await sut.SweepAsync().ConfigureAwait(false);
                AssertEqual(CaptainLogScreenSweepOutcomeEnum.Completed, result.Outcome);
                AssertEqual(1, result.Errors);
            });

            await RunTest("Sweep_Findings_LeavesTheMissionRecordUntouched", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "a tail with a finding";

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { new RecordingPass(Finding("rule_one", "evidence")) },
                    new RecordingNotePoster());

                await sut.SweepAsync().ConfigureAwait(false);

                Mission? after = await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertNotNull(after);
                AssertEqual(MissionStatusEnum.InProgress, after!.Status);
                AssertNull(after.FailureReason);
                AssertEqual(mission.CaptainId, after.CaptainId);
                AssertEqual(mission.LastUpdateUtc.ToString("O"), after.LastUpdateUtc.ToString("O"));
            });

            await RunTest("Sweep_PassReceivesTheTailWithItsHashAndByteCount", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await SeedInProgressMissionAsync(db, "voyage-a").ConfigureAwait(false);
                FakeTailReader reader = new FakeTailReader();
                reader.TailsByMissionId[mission.Id] = "measured tail";
                RecordingPass pass = new RecordingPass();

                CaptainLogScreenService sut = new CaptainLogScreenService(
                    CreateLogging(), db.Driver, EnabledSettings(), reader,
                    new List<ICaptainLogScreenPass> { pass });

                await sut.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, pass.Contexts.Count);
                LogScreenContext context = pass.Contexts[0];
                AssertEqual("measured tail", context.Tail);
                AssertEqual(mission.Id, context.MissionId);
                AssertEqual(mission.VoyageId, context.VoyageId);
                AssertEqual(64, context.TailSha256.Length);
                AssertEqual("measured tail".Length, context.TailBytes);
            });
        }

        private sealed class ConditionalThrowPass : ICaptainLogScreenPass
        {
            private readonly string _ThrowForMissionId;

            public ConditionalThrowPass(string throwForMissionId)
            {
                _ThrowForMissionId = throwForMissionId;
            }

            public string Name => "conditional-throw";

            public Task<IReadOnlyList<LogScreenFinding>> EvaluateAsync(LogScreenContext context, CancellationToken token)
            {
                if (context.MissionId == _ThrowForMissionId)
                    throw new InvalidOperationException("pass refused to evaluate this mission");
                return Task.FromResult<IReadOnlyList<LogScreenFinding>>(new List<LogScreenFinding>());
            }
        }
    }
}
