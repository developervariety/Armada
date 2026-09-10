namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests durable admission reservations for build-participating sibling lanes.
    /// </summary>
    public class SiblingLaneAdmissionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Sibling Lane Admission";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Separate service and database instances cannot reserve the same lane", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Vessel[] vessels = await CreateSiblingLaneAsync(testDb).ConfigureAwait(false);
                    using (SqliteDatabaseDriver secondDriver = new SqliteDatabaseDriver(testDb.ConnectionString, logging))
                    {
                        SiblingLaneAdmission first = new SiblingLaneAdmission(testDb.Driver, logging);
                        SiblingLaneAdmission second = new SiblingLaneAdmission(secondDriver, logging);
                        SiblingLaneReservation? held = await first.TryReserveAsync(
                            vessels[0], "msn_first").ConfigureAwait(false);
                        AssertNotNull(held, "first reservation");

                        try
                        {
                            SiblingLaneReservation? contending = await second.TryReserveAsync(
                                vessels[1], "msn_second").ConfigureAwait(false);
                            AssertNull(contending, "contending reservation");
                        }
                        finally
                        {
                            if (held != null) await held.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Disposal releases the complete lane for a later reservation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Vessel[] vessels = await CreateSiblingLaneAsync(testDb).ConfigureAwait(false);
                    SiblingLaneAdmission admission = new SiblingLaneAdmission(testDb.Driver, logging);
                    SiblingLaneReservation? first = await admission.TryReserveAsync(
                        vessels[0], "msn_first").ConfigureAwait(false);
                    AssertNotNull(first, "first reservation");
                    AssertEqual("vsl_lane_a,vsl_lane_b", JoinMembers(first!.Members), "reserved members");

                    await first.DisposeAsync().ConfigureAwait(false);
                    await first.DisposeAsync().ConfigureAwait(false);

                    SiblingLaneReservation? next = await admission.TryReserveAsync(
                        vessels[1], "msn_next").ConfigureAwait(false);
                    AssertNotNull(next, "reservation after release");
                    if (next != null) await next.DisposeAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<Vessel[]> CreateSiblingLaneAsync(TestDatabase testDb)
        {
            Vessel alpha = new Vessel("Lane A", "https://example.test/lane-a.git");
            alpha.Id = "vsl_lane_a";
            alpha.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
            {
                new SiblingRepo
                {
                    VesselRef = "Lane B",
                    RelativePath = "../LaneB",
                    BuildParticipant = true
                }
            });
            Vessel beta = new Vessel("Lane B", "https://example.test/lane-b.git");
            beta.Id = "vsl_lane_b";

            alpha = await testDb.Driver.Vessels.CreateAsync(alpha).ConfigureAwait(false);
            beta = await testDb.Driver.Vessels.CreateAsync(beta).ConfigureAwait(false);
            return new[] { alpha, beta };
        }

        private static string JoinMembers(IEnumerable<string> members)
        {
            return String.Join(",", members.OrderBy(item => item, StringComparer.Ordinal));
        }
    }
}
