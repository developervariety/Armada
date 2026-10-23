namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// REST record lookups: a GET-by-id route reads an abbreviated id as the one record the caller can see, refuses
    /// an ambiguous one with its candidates and answers 404 for an unknown one, as the MCP tools do; the objective
    /// list search matches record ids; and a list page larger than the limit is reported, not silently clamped.
    /// </summary>
    public class RecordLookupRouteTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "Record Lookup Routes";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion

        #region Protected-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("GetById_AnAbbreviatedIdReadsTheOneVisibleRecordRefusesTwinsAndMissesUnknown", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("abbreviated voyage")
                    {
                        Id = TestRecordIds.At("vyg", now, "onlyVoyage"),
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("abbreviated mission")
                    {
                        Id = TestRecordIds.At("msn", now + 10, "onlyMission"),
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VoyageId = voyage.Id
                    }).ConfigureAwait(false);
                    Objective objective = await harness.Driver.Objectives.CreateAsync(new Objective
                    {
                        Id = TestRecordIds.At("obj", now + 20, "onlyObjective"),
                        Title = "abbreviated objective",
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);

                    Dictionary<string, List<string>> twins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (string tail in new[] { "twinA", "twinB" })
                    {
                        Voyage twinVoyage = await harness.Driver.Voyages.CreateAsync(new Voyage("twin voyage " + tail)
                        {
                            Id = TestRecordIds.At("vyg", now + 30, tail),
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);
                        Mission twinMission = await harness.Driver.Missions.CreateAsync(new Mission("twin mission " + tail)
                        {
                            Id = TestRecordIds.At("msn", now + 40, tail),
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);
                        Objective twinObjective = await harness.Driver.Objectives.CreateAsync(new Objective
                        {
                            Id = TestRecordIds.At("obj", now + 50, tail),
                            Title = "twin objective " + tail,
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);
                        foreach (string id in new[] { twinVoyage.Id, twinMission.Id, twinObjective.Id })
                        {
                            string abbreviation = TestRecordIds.Abbreviate(id);
                            if (!twins.ContainsKey(abbreviation)) twins[abbreviation] = new List<string>();
                            twins[abbreviation].Add(id);
                        }
                    }

                    SurfaceReply voyageReply = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + TestRecordIds.Abbreviate(voyage.Id)).ConfigureAwait(false);
                    AssertEqual(200, voyageReply.Status, "an abbreviated voyage id reads the voyage: " + voyageReply);
                    VoyageDetail? detail = JsonSerializer.Deserialize<VoyageDetail>(voyageReply.Body, _Json);
                    AssertEqual(voyage.Id, detail?.Voyage?.Id, "the voyage is read by its full id");
                    AssertTrue(detail!.Missions.Any(item => item.Id == mission.Id), "the voyage's missions are listed under the full id: " + voyageReply);

                    SurfaceReply missionReply = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + TestRecordIds.Abbreviate(mission.Id)).ConfigureAwait(false);
                    AssertEqual(200, missionReply.Status, "an abbreviated mission id reads the mission: " + missionReply);
                    AssertEqual(mission.Id, JsonSerializer.Deserialize<Mission>(missionReply.Body, _Json)?.Id, "the mission is read by its full id");

                    SurfaceReply objectiveReply = await harness.RestAsync(HttpMethod.Get, "/api/v1/objectives/" + TestRecordIds.Abbreviate(objective.Id)).ConfigureAwait(false);
                    AssertEqual(200, objectiveReply.Status, "an abbreviated objective id reads the objective: " + objectiveReply);
                    AssertEqual(objective.Id, JsonSerializer.Deserialize<Objective>(objectiveReply.Body, _Json)?.Id, "the objective is read by its full id");

                    foreach (KeyValuePair<string, List<string>> twin in twins)
                    {
                        string route = RouteFor(twin.Key);
                        SurfaceReply rest = await harness.RestAsync(HttpMethod.Get, route + twin.Key).ConfigureAwait(false);
                        AssertEqual(409, rest.Status, twin.Key + ": an ambiguous abbreviation is refused: " + rest);
                        AmbiguousRefusal? refusal = JsonSerializer.Deserialize<AmbiguousRefusal>(rest.Body, _Json);
                        AssertEqual("ambiguous_id", refusal?.Data?.Reason, twin.Key + ": the refusal names the reason: " + rest);
                        AssertEqual(String.Join(",", twin.Value.OrderBy(id => id, StringComparer.Ordinal)), String.Join(",", refusal!.Data!.Candidates),
                            twin.Key + ": the refusal lists every candidate");

                        SurfaceReply mcp = await harness.McpAsync(McpToolFor(twin.Key), McpArgumentsFor(twin.Key)).ConfigureAwait(false);
                        AssertContains("ambiguous_id", mcp.Body, twin.Key + ": MCP refuses the same abbreviation");
                        foreach (string candidate in twin.Value)
                            AssertContains(candidate, mcp.Body, twin.Key + ": MCP lists the same candidates");
                    }

                    foreach (string prefix in new[] { "vyg", "msn", "obj" })
                    {
                        string unknown = TestRecordIds.Abbreviate(TestRecordIds.At(prefix, now + 60, "absent"));
                        SurfaceReply rest = await harness.RestAsync(HttpMethod.Get, RouteFor(unknown) + unknown).ConfigureAwait(false);
                        AssertEqual(404, rest.Status, unknown + ": an abbreviation with no visible record is not found: " + rest);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("ObjectiveList_SearchMatchesTheIdAndItsAbbreviation", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Objective wanted = await harness.Driver.Objectives.CreateAsync(new Objective
                    {
                        Id = TestRecordIds.At("obj", now, "wantedRow"),
                        Title = "first backlog row",
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    await harness.Driver.Objectives.CreateAsync(new Objective
                    {
                        Id = TestRecordIds.At("obj", now + 1000, "otherRow"),
                        Title = "second backlog row",
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);

                    foreach (string search in new[] { wanted.Id, TestRecordIds.Abbreviate(wanted.Id) })
                    {
                        SurfaceReply reply = await harness.RestAsync(HttpMethod.Get, "/api/v1/objectives?search=" + Uri.EscapeDataString(search)).ConfigureAwait(false);
                        AssertEqual(200, reply.Status, search + ": " + reply);
                        ObjectivePage? page = JsonSerializer.Deserialize<ObjectivePage>(reply.Body, _Json);
                        AssertEqual(wanted.Id, String.Join(",", page!.Objects.Select(item => item.Id)), search + ": the search finds exactly the objective with that id");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("ObjectiveList_ReportsAPageSizeAboveTheLimit", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    await harness.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "only backlog row",
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);

                    SurfaceReply over = await harness.RestAsync(HttpMethod.Get, "/api/v1/objectives?pageSize=1000").ConfigureAwait(false);
                    AssertEqual(200, over.Status, over.ToString());
                    ObjectivePage? clamped = JsonSerializer.Deserialize<ObjectivePage>(over.Body, _Json);
                    AssertEqual(500, clamped!.PageSize, "the page is served at the limit");
                    AssertEqual((int?)1000, clamped.RequestedPageSize, "the envelope reports the page size asked for: " + over);
                    AssertEqual((int?)500, clamped.MaxPageSize, "the envelope reports the limit: " + over);

                    SurfaceReply within = await harness.RestAsync(HttpMethod.Get, "/api/v1/objectives?pageSize=10").ConfigureAwait(false);
                    ObjectivePage? served = JsonSerializer.Deserialize<ObjectivePage>(within.Body, _Json);
                    AssertEqual(10, served!.PageSize, "a page size within the limit is served as asked");
                    AssertFalse(within.Body.Contains("RequestedPageSize", StringComparison.Ordinal), "nothing is reported when the request is within the limit: " + within);
                }
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static string RouteFor(string id)
        {
            if (id.StartsWith("vyg_", StringComparison.Ordinal)) return "/api/v1/voyages/";
            if (id.StartsWith("msn_", StringComparison.Ordinal)) return "/api/v1/missions/";
            return "/api/v1/objectives/";
        }

        private static string McpToolFor(string id)
        {
            if (id.StartsWith("vyg_", StringComparison.Ordinal)) return "armada_voyage_status";
            if (id.StartsWith("msn_", StringComparison.Ordinal)) return "armada_mission_status";
            return "get_objective";
        }

        private static object McpArgumentsFor(string id)
        {
            if (id.StartsWith("vyg_", StringComparison.Ordinal)) return new { voyageId = id };
            if (id.StartsWith("msn_", StringComparison.Ordinal)) return new { missionId = id };
            return new { objectiveId = id };
        }

        #endregion

        #region Private-Classes

        private sealed class VoyageDetail
        {
            public Voyage? Voyage { get; set; }

            public List<Mission> Missions { get; set; } = new List<Mission>();
        }

        private sealed class AmbiguousRefusal
        {
            public AmbiguousData? Data { get; set; }
        }

        private sealed class AmbiguousData
        {
            public string? Reason { get; set; }

            public List<string> Candidates { get; set; } = new List<string>();
        }

        private sealed class ObjectivePage
        {
            public int PageSize { get; set; }

            public int? RequestedPageSize { get; set; }

            public int? MaxPageSize { get; set; }

            public List<Objective> Objects { get; set; } = new List<Objective>();
        }

        #endregion
    }
}
