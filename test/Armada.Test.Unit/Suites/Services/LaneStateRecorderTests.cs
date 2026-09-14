namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Tests for durable lane state transition recording.</summary>
    public sealed class LaneStateRecorderTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Lane State Recorder";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("SamplesGroupEligibleWorkAndOccupancyByLane", () =>
            {
                VesselLaneMap lanes = VesselLaneMap.Build(new[]
                {
                    new Vessel("alpha", "https://example.test/alpha.git") { Id = "vsl_alpha" },
                    new Vessel("solo", "https://example.test/solo.git") { Id = "vsl_solo" }
                });
                List<Objective> eligible = new List<Objective>
                {
                    new Objective { Title = "one", VesselIds = new List<string> { "vsl_alpha" }, Tags = new List<string> { "port:ecu" } },
                    new Objective { Title = "two", VesselIds = new List<string> { "vsl_alpha" }, Tags = new List<string> { "port:dxp" } },
                    new Objective { Title = "wide", VesselIds = new List<string> { "vsl_alpha", "vsl_solo" } }
                };

                List<LaneStateTransition> samples = LaneStateRecorder.BuildSamples(
                    lanes, eligible, members => members.Contains("vsl_solo") ? 1 : 0, 2, LaneBlockReasonEnum.None, new[] { "vsl_solo" });

                AssertEqual(2, samples.Count, "an eligible lane and an occupied lane are both sampled");
                LaneStateTransition alpha = samples.Single(item => item.LaneKey == "vsl_alpha");
                AssertEqual(2, alpha.EligibleCount, "a multi-vessel objective belongs to no lane");
                AssertEqual("dxp,ecu", alpha.EligibleSourceFamilies);
                AssertEqual(2, alpha.Capacity);
                LaneStateTransition solo = samples.Single(item => item.LaneKey == "vsl_solo");
                AssertEqual(0, solo.EligibleCount);
                AssertEqual(1, solo.Occupied);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ObservationsWriteChangesCheckpointsAndVanishedLanes", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                LaneStateRecorder recorder = new LaneStateRecorder(testDb.Driver, null, () => now);

                AssertEqual(1, await recorder.ObserveAsync(new List<LaneStateTransition> { Sample("vsl_a", 1, 0) }, 600).ConfigureAwait(false), "the first observation is written");
                now = now.AddMinutes(1);
                AssertEqual(0, await recorder.ObserveAsync(new List<LaneStateTransition> { Sample("vsl_a", 1, 0) }, 600).ConfigureAwait(false), "an unchanged state inside the checkpoint interval is not rewritten");
                now = now.AddMinutes(5);
                AssertEqual(1, await recorder.ObserveAsync(new List<LaneStateTransition> { Sample("vsl_a", 1, 0) }, 600).ConfigureAwait(false), "a checkpoint is written before the trust window expires");
                now = now.AddMinutes(1);
                AssertEqual(1, await recorder.ObserveAsync(new List<LaneStateTransition> { Sample("vsl_a", 1, 1) }, 600).ConfigureAwait(false), "an occupancy change is written");
                now = now.AddMinutes(1);
                AssertEqual(1, await recorder.ObserveAsync(new List<LaneStateTransition>(), 600).ConfigureAwait(false), "a lane that disappears is written once as empty");
                now = now.AddMinutes(1);
                AssertEqual(0, await recorder.ObserveAsync(new List<LaneStateTransition>(), 600).ConfigureAwait(false), "an empty lane is not rewritten");

                ProductionFactPage<LaneStateTransition> page = await testDb.Driver.LaneStateTransitions.EnumerateAsync(new ProductionFactQuery
                {
                    FromUtc = now.AddHours(-1),
                    ToUtc = now.AddHours(1)
                }).ConfigureAwait(false);
                AssertEqual(4, page.Items.Count);
                AssertFalse(page.Items[0].Checkpoint);
                AssertTrue(page.Items[1].Checkpoint, "an unchanged rewrite is marked as a checkpoint");
                AssertEqual(1, page.Items[2].Occupied);
                AssertEqual(0, page.Items[3].EligibleCount);
                AssertEqual(0, page.Items[3].Occupied);
                AssertEqual(600, page.Items[3].ValidForSeconds);
            }).ConfigureAwait(false);

            await RunTest("TrustWindowIsTwiceTheSweepInterval", () =>
            {
                AssertEqual(120, LaneStateRecorder.TrustWindowSeconds(TimeSpan.FromSeconds(10)), "short intervals use a one-minute floor");
                AssertEqual(1200, LaneStateRecorder.TrustWindowSeconds(TimeSpan.FromMinutes(10)));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        private static LaneStateTransition Sample(string lane, int eligible, int occupied)
        {
            return new LaneStateTransition { LaneKey = lane, EligibleCount = eligible, Occupied = occupied, Capacity = 1 };
        }
    }
}
