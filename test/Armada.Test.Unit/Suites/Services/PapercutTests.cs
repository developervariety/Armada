namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Armada.Server.Mcp.Tools;

    /// <summary>
    /// Covers papercut parsing, sanitizing, event round-trip, and grouping.
    /// </summary>
    public class PapercutTests : TestSuite
    {
        public override string Name => "Papercuts";

        protected override async Task RunTestsAsync()
        {
            await RunTest("TryParseLine NonPapercutMarker ReturnsNull", () =>
            {
                AssertNull(PapercutParser.TryParseLine("[ARMADA:PROGRESS] 50"));
            });

            await RunTest("TryParseLine PlainLine ReturnsNull", () =>
            {
                AssertNull(PapercutParser.TryParseLine("just a log line"));
            });

            await RunTest("TryParseLine Json ParsesEveryField", () =>
            {
                Papercut? papercut = PapercutParser.TryParseLine(
                    "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Medium\",\"title\":\"README build command is wrong\",\"detail\":\"It names make, the repo uses dotnet\",\"path\":\"README.md\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.MissingDoc, papercut!.Category);
                AssertEqual(PapercutSeverityEnum.Medium, papercut.Severity);
                AssertEqual("README build command is wrong", papercut.Title);
                AssertEqual("README.md", papercut.Path);
            });

            await RunTest("TryParseLine FindsPapercutAfterAnotherMarkerInOneRecord", () =>
            {
                Papercut? papercut = PapercutParser.TryParseLine(
                    "[ARMADA:MESSAGE] Finished\n[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"Second marker\"}");

                AssertNotNull(papercut, "A papercut that is not the first marker of the record is still found");
                AssertEqual("Second marker", papercut!.Title);
            });

            await RunTest("TryParseValue UnknownCategory FallsBackToOther", () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue("{\"category\":\"Nonsense\",\"title\":\"something\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.Other, papercut!.Category);
            });

            await RunTest("TryParseValue UnknownSeverity FallsBackToLow", () =>
            {
                // A formatting slip must never outrank a real defect, so an unreadable severity is Low.
                Papercut? papercut = PapercutParser.TryParseValue("{\"severity\":\"Catastrophic\",\"title\":\"something\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutSeverityEnum.Low, papercut!.Severity);
            });

            await RunTest("TryParseValue CategoryWithSpacing Resolves", () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue("{\"category\":\"broken link\",\"title\":\"dead link in docs\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.BrokenLink, papercut!.Category);
            });

            await RunTest("TryParseValue PipeForm Parses", () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue("ToolFailure|High|dotnet test hangs on the Api project");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.ToolFailure, papercut!.Category);
                AssertEqual(PapercutSeverityEnum.High, papercut.Severity);
                AssertEqual("dotnet test hangs on the Api project", papercut.Title);
            });

            await RunTest("TryParseValue BareText BecomesOtherLow", () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue("the sibling repo was missing");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.Other, papercut!.Category);
                AssertEqual(PapercutSeverityEnum.Low, papercut.Severity);
                AssertEqual("the sibling repo was missing", papercut.Title);
            });

            await RunTest("TryParseValue TruncatedJson ReturnsNull", () =>
            {
                // A line that opens as JSON and does not close is a truncated report, not a title.
                AssertNull(PapercutParser.TryParseValue("{\"category\":\"ToolFailure\",\"title\":\"dotnet te"));
            });

            await RunTest("TryParseValue EmptyTitle ReturnsNull", () =>
            {
                AssertNull(PapercutParser.TryParseValue("{\"category\":\"ToolFailure\",\"title\":\"   \"}"));
            });

            await RunTest("TryParseValue TemplateExample ReturnsNull", () =>
            {
                // The brief instruction template shows this literal example so captains can see the
                // report shape. It is not a report: a scan that walks the log instructions directory
                // counts it 269 times as a phantom MissingDoc, and a captain may echo it verbatim
                // while testing the format. Neither may reach the store.
                AssertNull(PapercutParser.TryParseValue(
                    "{\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"one line\",\"detail\":\"optional\",\"path\":\"optional/file.cs\"}"));

                AssertNull(PapercutParser.TryParseLine(
                    "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"one line\",\"detail\":\"optional\",\"path\":\"optional/file.cs\"}"));
            });

            await RunTest("TryParseValue RealReportStillParses", () =>
            {
                // The placeholder rejection must not catch a genuine report whose wording merely
                // resembles the example.
                Papercut? papercut = PapercutParser.TryParseValue(
                    "{\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"one line README is missing\",\"detail\":\"the real detail\",\"path\":\"README.md\"}");
                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.MissingDoc, papercut!.Category);
            });

            await RunTest("TryParseValue RedactsCredentials", () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue(
                    "{\"title\":\"auth failed\",\"detail\":\"ran curl -H token=abc123secretvalue against the api\"}");

                AssertNotNull(papercut);
                AssertContains("[redacted]", papercut!.Detail!);
                AssertFalse(papercut.Detail!.Contains("abc123secretvalue"));
            });

            await RunTest("Title IsClampedToLimit", () =>
            {
                string longTitle = new String('x', Papercut.MaxTitleChars + 200);
                Papercut? papercut = PapercutParser.TryParseValue("{\"title\":\"" + longTitle + "\"}");

                AssertNotNull(papercut);
                AssertTrue(papercut!.Title.Length <= Papercut.MaxTitleChars + 3);
            });

            await RunTest("ToEvent FromEvent RoundTrips", () =>
            {
                Papercut original = new Papercut();
                original.Category = PapercutCategoryEnum.EnvSetup;
                original.Severity = PapercutSeverityEnum.High;
                original.Title = "sibling repo ExampleSibling was not provisioned";
                original.Detail = "the build needs it one level up";
                original.Path = "src/Thing.csproj";
                original.CaptainId = "cpt_one";
                original.MissionId = "msn_one";
                original.VesselId = "vsl_one";
                original.VoyageId = "vyg_one";
                original.Runtime = "ClaudeCode";

                ArmadaEvent evt = PapercutService.ToEvent(original);
                AssertEqual("papercut", evt.EventType);
                AssertEqual("vsl_one", evt.VesselId);

                Papercut? restored = PapercutService.TryFromEvent(evt);
                AssertNotNull(restored);
                AssertEqual(PapercutCategoryEnum.EnvSetup, restored!.Category);
                AssertEqual(PapercutSeverityEnum.High, restored.Severity);
                AssertEqual(original.Title, restored.Title);
                AssertEqual("msn_one", restored.MissionId);
                AssertEqual("ClaudeCode", restored.Runtime);
            });

            await RunTest("TryFromEvent OtherEventType ReturnsNull", () =>
            {
                ArmadaEvent evt = new ArmadaEvent("mission.completed", "not a papercut");
                evt.Payload = "{\"title\":\"something\"}";
                AssertNull(PapercutService.TryFromEvent(evt));
            });

            await RunTest("NormalizeTitle StripsIdsAndNumbers", () =>
            {
                string first = PapercutService.NormalizeTitle("Mission msn_abc123 failed after 42 seconds");
                string second = PapercutService.NormalizeTitle("Mission msn_zzz999 failed after 91 seconds");
                AssertEqual(first, second);
            });

            await RunTest("Group CollapsesSameFrictionAcrossCaptains", () =>
            {
                List<Papercut> papercuts = new List<Papercut>
                {
                    BuildPapercut("vsl_one", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Low,
                        "Build command in README is stale (line 12)", "cpt_one", "msn_one", DateTime.UtcNow.AddHours(-3)),
                    BuildPapercut("vsl_one", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Medium,
                        "Build command in README is stale (line 87)", "cpt_two", "msn_two", DateTime.UtcNow.AddHours(-1)),
                    BuildPapercut("vsl_two", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Low,
                        "Build command in README is stale (line 12)", "cpt_one", "msn_three", DateTime.UtcNow)
                };

                List<PapercutGroup> groups = PapercutService.Group(papercuts);

                AssertEqual(2, groups.Count);
                AssertEqual(2, groups[0].Count);
                AssertEqual(2, groups[0].DistinctCaptainCount);
                AssertEqual("vsl_one", groups[0].VesselId);

                // The group carries the worst severity anyone reported, not the first one seen.
                AssertEqual(PapercutSeverityEnum.Medium, groups[0].HighestSeverity);
                AssertEqual(2, groups[0].SampleMissionIds.Count);
            });

            await RunTest("Group SeparatesDifferentCategories", () =>
            {
                List<Papercut> papercuts = new List<Papercut>
                {
                    BuildPapercut("vsl_one", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Low,
                        "same words entirely", "cpt_one", "msn_one", DateTime.UtcNow),
                    BuildPapercut("vsl_one", PapercutCategoryEnum.ToolFailure, PapercutSeverityEnum.Low,
                        "same words entirely", "cpt_one", "msn_two", DateTime.UtcNow)
                };

                AssertEqual(2, PapercutService.Group(papercuts).Count);
            });

            await RunTest("Group EmptyInput ReturnsEmpty", () =>
            {
                AssertEqual(0, PapercutService.Group(null).Count);
                AssertEqual(0, PapercutService.Group(new List<Papercut>()).Count);
            });

            await RunTest("ListAsync UngroupedContinuationCoversMoreThan200Reports", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await AddPapercutsAsync(testDb.Driver, 205, true).ConfigureAwait(false);
                    PapercutListResponse first = await ListPageAsync(testDb.Driver, true, null).ConfigureAwait(false);
                    AssertEqual(200, first.Papercuts.Count);
                    AssertEqual(205, first.TotalFiltered);
                    AssertTrue(first.HasMore);
                    await AddPapercutsAsync(testDb.Driver, 1).ConfigureAwait(false);

                    PapercutListResponse second = await ListPageAsync(testDb.Driver, true, first.NextContinuationToken).ConfigureAwait(false);
                    AssertEqual(5, second.Papercuts.Count);
                    AssertEqual(205, second.TotalFiltered);
                    AssertFalse(second.HasMore);
                    HashSet<string> eventIds = first.Papercuts.Select(report => report.EventId!).ToHashSet(StringComparer.Ordinal);
                    foreach (Papercut report in second.Papercuts)
                        AssertTrue(eventIds.Add(report.EventId!), "Continuation pages do not repeat reports");
                    AssertEqual(205, eventIds.Count);
                }
            });

            await RunTest("ListAsync GroupedContinuationCoversMoreThan200Groups", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await AddPapercutsAsync(testDb.Driver, 205, true).ConfigureAwait(false);
                    PapercutListResponse first = await ListPageAsync(testDb.Driver, false, null).ConfigureAwait(false);
                    AssertEqual(200, first.Groups.Count);
                    AssertEqual(205, first.TotalGroups);
                    AssertTrue(first.HasMore);
                    await AddPapercutsAsync(testDb.Driver, 1).ConfigureAwait(false);

                    PapercutListResponse second = await ListPageAsync(testDb.Driver, false, first.NextContinuationToken).ConfigureAwait(false);
                    AssertEqual(5, second.Groups.Count);
                    AssertEqual(205, second.TotalGroups);
                    AssertFalse(second.HasMore);
                    HashSet<string> keys = first.Groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (PapercutGroup group in second.Groups)
                        AssertTrue(keys.Add(group.Key), "Continuation pages do not repeat groups");
                    AssertEqual(205, keys.Count);
                }
            });

            await RunTest("ListAsync ContinuationRejectsInvalidAndChangedFilters", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await AddPapercutsAsync(testDb.Driver, 205).ConfigureAwait(false);
                    PapercutListResponse first = await ListPageAsync(testDb.Driver, true, null).ConfigureAwait(false);
                    PapercutListResponse mismatched = await ListPageAsync(testDb.Driver, false, first.NextContinuationToken).ConfigureAwait(false);
                    AssertContains("does not match", mismatched.Error!);
                    PapercutListResponse invalid = await ListPageAsync(testDb.Driver, true, "not-a-token").ConfigureAwait(false);
                    AssertEqual("Invalid continuationToken", invalid.Error);
                }
            });

            await RunTest("ListAsync ScanLimitReportsTruncationSeparately", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await AddPapercutsAsync(testDb.Driver, 205).ConfigureAwait(false);
                    PapercutListResponse result = await ListPageAsync(testDb.Driver, true, null, 100, 100).ConfigureAwait(false);
                    AssertEqual(100, result.Scanned);
                    AssertEqual(205L, result.TotalEvents);
                    AssertEqual(100, result.TotalFiltered);
                    AssertTrue(result.ScanTruncated);
                    AssertFalse(result.HasMore);
                }
            });

            await RunTest("ListAsync ChangedSourceRestartsContinuation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await AddPapercutsAsync(testDb.Driver, 205).ConfigureAwait(false);
                    PapercutListResponse first = await ListPageAsync(testDb.Driver, true, null).ConfigureAwait(false);
                    await testDb.Driver.Events.DeleteAsync(first.Papercuts[0].EventId!).ConfigureAwait(false);
                    PapercutListResponse second = await ListPageAsync(testDb.Driver, true, first.NextContinuationToken).ConfigureAwait(false);
                    AssertContains("scan changed", second.Error!);
                }
            });

            await RunTest("ListAsync DefaultGroupsOneRowPerVessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    GroupingFixture fixture = await SeedGroupingFixtureAsync(testDb.Driver).ConfigureAwait(false);
                    PapercutListResponse result = await ListAsync(testDb.Driver, new { }).ConfigureAwait(false);

                    AssertNull(result.Error);
                    AssertEqual("vessel", result.GroupBy);
                    AssertEqual(9, result.TotalFiltered);
                    AssertEqual(3, result.TotalGroups);
                    AssertEqual(3, result.Groups.Count);

                    PapercutGroup alpha = result.Groups[0];
                    AssertEqual(fixture.AlphaId, alpha.Key);
                    AssertEqual(PapercutGroupingEnum.Vessel, alpha.GroupBy);
                    AssertEqual(fixture.AlphaId, alpha.VesselId);
                    AssertEqual("alpha-vessel", alpha.VesselName);
                    AssertEqual(5, alpha.Count);
                    AssertEqual(3, alpha.DistinctCaptainCount);
                    AssertEqual(PapercutSeverityEnum.High, alpha.HighestSeverity);
                    AssertEqual(PapercutCategoryEnum.ToolFailure, alpha.Category);
                    AssertEqual(fixture.Now.AddMinutes(-50), alpha.FirstSeenUtc);
                    AssertEqual(fixture.Now.AddMinutes(-10), alpha.LastSeenUtc);
                    AssertEqual(2, alpha.CategoryCounts.Count);
                    AssertEqual(3, alpha.CategoryCounts["ToolFailure"]);
                    AssertEqual(2, alpha.CategoryCounts["MissingDoc"]);
                    AssertEqual(3, alpha.TitleGroupCount);
                    AssertEqual(3, alpha.TitleSubgroups.Count);
                    AssertEqual(3, alpha.TitleSubgroups[0].Count);
                    AssertEqual("build step # times out", alpha.TitleSubgroups[0].NormalizedTitle);
                    AssertEqual("build step 3 times out", alpha.TitleSubgroups[0].SampleTitle);
                    AssertEqual(PapercutCategoryEnum.ToolFailure, alpha.TitleSubgroups[0].Category);
                    AssertEqual(fixture.Now.AddMinutes(-20), alpha.TitleSubgroups[0].LastSeenUtc);
                    AssertEqual("changelog omits setup", alpha.TitleSubgroups[1].NormalizedTitle);
                    AssertEqual("readme omits setup", alpha.TitleSubgroups[2].NormalizedTitle);
                    AssertEqual("CHANGELOG omits setup", alpha.SampleTitle);
                    AssertEqual("msn_a5", alpha.SampleMissionIds[0]);
                    AssertEqual(5, alpha.SampleMissionIds.Count);

                    AssertEqual(fixture.BravoId, result.Groups[1].Key);
                    AssertEqual("bravo-vessel", result.Groups[1].VesselName);
                    AssertEqual(3, result.Groups[1].Count);
                    AssertEqual(1, result.Groups[1].DistinctCaptainCount);
                    AssertEqual("vsl_unregistered", result.Groups[2].Key);
                    AssertNull(result.Groups[2].VesselName);
                }
            });

            await RunTest("ListAsync CategoryGroupsOneRowPerVesselAndCategory", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    GroupingFixture fixture = await SeedGroupingFixtureAsync(testDb.Driver).ConfigureAwait(false);
                    PapercutListResponse result = await ListAsync(testDb.Driver, new { groupBy = "category" }).ConfigureAwait(false);

                    AssertEqual("category", result.GroupBy);
                    AssertEqual(4, result.TotalGroups);
                    PapercutGroup alphaTools = result.Groups.Single(g => g.Key == fixture.AlphaId + "|ToolFailure");
                    AssertEqual(PapercutGroupingEnum.Category, alphaTools.GroupBy);
                    AssertEqual(PapercutCategoryEnum.ToolFailure, alphaTools.Category);
                    AssertEqual(3, alphaTools.Count);
                    AssertEqual(PapercutSeverityEnum.High, alphaTools.HighestSeverity);
                    AssertEqual(1, alphaTools.CategoryCounts.Count);
                    AssertEqual(1, alphaTools.TitleGroupCount);
                    AssertEqual("build step # times out", alphaTools.TitleSubgroups.Single().NormalizedTitle);

                    PapercutGroup alphaDocs = result.Groups.Single(g => g.Key == fixture.AlphaId + "|MissingDoc");
                    AssertEqual(2, alphaDocs.Count);
                    AssertEqual(2, alphaDocs.TitleGroupCount);
                    AssertEqual(1, alphaDocs.DistinctCaptainCount);
                    AssertEqual("alpha-vessel", alphaDocs.VesselName);
                    AssertEqual(3, result.Groups[0].Count);
                }
            });

            await RunTest("ListAsync TitleGroupingKeepsOneRowPerTitle", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    GroupingFixture fixture = await SeedGroupingFixtureAsync(testDb.Driver).ConfigureAwait(false);
                    PapercutListResponse result = await ListAsync(testDb.Driver, new { groupBy = "Title" }).ConfigureAwait(false);

                    AssertEqual("title", result.GroupBy);
                    AssertEqual(5, result.TotalGroups);
                    PapercutGroup top = result.Groups[0];
                    AssertEqual(fixture.AlphaId + "|ToolFailure|build step # times out", top.Key);
                    AssertEqual(PapercutGroupingEnum.Title, top.GroupBy);
                    AssertEqual(3, top.Count);
                    AssertEqual(0, top.TitleSubgroups.Count);
                }
            });

            await RunTest("ListAsync FiltersApplyBeforeVesselGrouping", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    GroupingFixture fixture = await SeedGroupingFixtureAsync(testDb.Driver).ConfigureAwait(false);
                    PapercutListResponse byCategory = await ListAsync(testDb.Driver, new { category = "MissingDoc" }).ConfigureAwait(false);
                    AssertEqual(2, byCategory.TotalGroups);
                    PapercutGroup alpha = byCategory.Groups.Single(g => g.Key == fixture.AlphaId);
                    AssertEqual(2, alpha.Count);
                    AssertEqual(PapercutCategoryEnum.MissingDoc, alpha.Category);
                    AssertEqual(1, alpha.CategoryCounts.Count);
                    AssertEqual(PapercutSeverityEnum.Medium, alpha.HighestSeverity);

                    PapercutListResponse bySeverity = await ListAsync(testDb.Driver, new { minSeverity = "High", vesselId = fixture.AlphaId }).ConfigureAwait(false);
                    AssertEqual(1, bySeverity.TotalGroups);
                    AssertEqual(1, bySeverity.Groups[0].Count);

                    PapercutListResponse recent = await ListAsync(testDb.Driver, new { sinceHours = 1 }).ConfigureAwait(false);
                    AssertEqual(2, recent.TotalGroups);
                    AssertEqual(5, recent.Groups[0].Count);
                }
            });

            await RunTest("ListAsync VesselGroupCarriesTenLargestTitleSubgroups", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DateTime now = DateTime.UtcNow.AddMinutes(-1);
                    for (int i = 0; i < 12; i++)
                    {
                        for (int copy = 0; copy <= i % 3; copy++)
                        {
                            Papercut papercut = BuildPapercut("vsl_wide", PapercutCategoryEnum.RepoFriction, PapercutSeverityEnum.Low,
                                "friction " + AlphaCode(i), "cpt_w" + copy, "msn_w" + i + "_" + copy, now.AddSeconds(-i));
                            await testDb.Driver.Events.CreateAsync(PapercutService.ToEvent(papercut), CancellationToken.None).ConfigureAwait(false);
                        }
                    }

                    PapercutListResponse result = await ListAsync(testDb.Driver, new { }).ConfigureAwait(false);
                    PapercutGroup wide = result.Groups.Single();
                    AssertEqual(24, wide.Count);
                    AssertEqual(12, wide.TitleGroupCount);
                    AssertEqual(PapercutService.MaxTitleSubgroups, wide.TitleSubgroups.Count);
                    AssertEqual("friction c", wide.TitleSubgroups[0].NormalizedTitle);
                    AssertEqual("friction f", wide.TitleSubgroups[1].NormalizedTitle);
                    for (int i = 1; i < wide.TitleSubgroups.Count; i++)
                    {
                        PapercutTitleSubgroup previous = wide.TitleSubgroups[i - 1];
                        PapercutTitleSubgroup current = wide.TitleSubgroups[i];
                        AssertTrue(previous.Count > current.Count ||
                            (previous.Count == current.Count && previous.LastSeenUtc >= current.LastSeenUtc),
                            "Subgroups are ordered by count, then by most recent report");
                    }
                }
            });

            await RunTest("ListAsync ContinuationPagesEveryGroupingModeAndRejectsAnotherMode", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedGroupingFixtureAsync(testDb.Driver).ConfigureAwait(false);
                    Dictionary<string, int> expectedGroups = new Dictionary<string, int> { ["vessel"] = 3, ["category"] = 4, ["title"] = 5 };
                    Dictionary<string, string> tokens = new Dictionary<string, string>();
                    foreach (KeyValuePair<string, int> mode in expectedGroups)
                    {
                        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
                        int reports = 0;
                        string? token = null;
                        int pages = 0;
                        do
                        {
                            PapercutListResponse page = await ListAsync(testDb.Driver, new { groupBy = mode.Key, limit = 2, continuationToken = token }).ConfigureAwait(false);
                            AssertNull(page.Error, mode.Key + " page " + pages);
                            AssertEqual(mode.Key, page.GroupBy);
                            AssertEqual(mode.Value, page.TotalGroups);
                            foreach (PapercutGroup group in page.Groups)
                            {
                                AssertTrue(keys.Add(group.Key), "Continuation pages do not repeat " + mode.Key + " groups");
                                reports += group.Count;
                            }
                            if (pages == 0) tokens[mode.Key] = page.NextContinuationToken!;
                            token = page.NextContinuationToken;
                            pages++;
                        }
                        while (token != null);
                        AssertEqual(mode.Value, keys.Count);
                        AssertEqual(9, reports);
                        AssertEqual((mode.Value + 1) / 2, pages);
                    }

                    PapercutListResponse defaulted = await ListAsync(testDb.Driver, new { limit = 2, continuationToken = tokens["vessel"] }).ConfigureAwait(false);
                    AssertNull(defaulted.Error);
                    AssertEqual(1, defaulted.Groups.Count);

                    PapercutListResponse vesselTokenAsTitle = await ListAsync(testDb.Driver, new { groupBy = "title", limit = 2, continuationToken = tokens["vessel"] }).ConfigureAwait(false);
                    AssertContains("grouping mode", vesselTokenAsTitle.Error!);
                    AssertContains("'vessel'", vesselTokenAsTitle.Error!);
                    AssertContains("'title'", vesselTokenAsTitle.Error!);

                    PapercutListResponse titleTokenAsDefault = await ListAsync(testDb.Driver, new { limit = 2, continuationToken = tokens["title"] }).ConfigureAwait(false);
                    AssertContains("grouping mode", titleTokenAsDefault.Error!);

                    PapercutListResponse categoryTokenUngrouped = await ListAsync(testDb.Driver, new { ungrouped = true, limit = 2, continuationToken = tokens["category"] }).ConfigureAwait(false);
                    AssertContains("'ungrouped'", categoryTokenUngrouped.Error!);

                    PapercutListResponse unknown = await ListAsync(testDb.Driver, new { groupBy = "captain" }).ConfigureAwait(false);
                    AssertContains("Unknown groupBy", unknown.Error!);
                }
            });

            await RunTest("BriefSection NamesTheMarkerAndTheLimit", () =>
            {
                string section = MissionService.BuildPapercutsSection();
                AssertContains("[ARMADA:PAPERCUT]", section);
                AssertContains("Report it, do not fix it", section);
            });
        }

        private static Papercut BuildPapercut(
            string vesselId,
            PapercutCategoryEnum category,
            PapercutSeverityEnum severity,
            string title,
            string captainId,
            string missionId,
            DateTime reportedUtc)
        {
            Papercut papercut = new Papercut();
            papercut.VesselId = vesselId;
            papercut.Category = category;
            papercut.Severity = severity;
            papercut.Title = title;
            papercut.CaptainId = captainId;
            papercut.MissionId = missionId;
            papercut.ReportedUtc = reportedUtc;
            return papercut;
        }

        private static async Task AddPapercutsAsync(DatabaseDriver database, int count, bool sameTimestamp = false)
        {
            DateTime sharedTimestamp = DateTime.UtcNow.AddMinutes(-1);
            for (int i = 0; i < count; i++)
            {
                Papercut papercut = BuildPapercut("vsl_paging", PapercutCategoryEnum.ToolFailure, PapercutSeverityEnum.Low,
                    "unique friction " + AlphaCode(i), "cpt_" + i.ToString("D3"), "msn_" + i.ToString("D3"),
                    sameTimestamp ? sharedTimestamp : DateTime.UtcNow.AddSeconds(-i));
                await database.Events.CreateAsync(PapercutService.ToEvent(papercut), CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static Task<PapercutListResponse> ListPageAsync(DatabaseDriver database, bool ungrouped, string? continuationToken, int limit = 200, int scanLimit = 500, string? groupBy = "title")
        {
            return ListAsync(database, new { ungrouped, limit, scanLimit, continuationToken, groupBy });
        }

        private static async Task<PapercutListResponse> ListAsync(DatabaseDriver database, object arguments)
        {
            using (JsonDocument args = JsonDocument.Parse(JsonSerializer.Serialize(arguments)))
            {
                object result = await McpPapercutTools.ListAsync(args.RootElement, database, null, CancellationToken.None).ConfigureAwait(false);
                return JsonSerializer.Deserialize<PapercutListResponse>(JsonSerializer.Serialize(result), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!;
            }
        }

        private sealed class PapercutListResponse
        {
            public PapercutListResponse()
            {
            }

            public List<Papercut> Papercuts { get; set; } = new List<Papercut>();
            public List<PapercutGroup> Groups { get; set; } = new List<PapercutGroup>();
            public string? GroupBy { get; set; }
            public int Scanned { get; set; }
            public int Matched { get; set; }
            public int TotalFiltered { get; set; }
            public int TotalGroups { get; set; }
            public long TotalEvents { get; set; }
            public bool ScanTruncated { get; set; }
            public bool HasMore { get; set; }
            public string? NextContinuationToken { get; set; }
            public string? Error { get; set; }
        }

        private sealed class GroupingFixture
        {
            public string AlphaId { get; set; } = String.Empty;
            public string BravoId { get; set; } = String.Empty;
            public DateTime Now { get; set; }
        }

        // Three vessels and three categories. Alpha: one ToolFailure title reported three times with
        // different numbers by three captains (one High), and two distinct MissingDoc titles. Bravo:
        // three reports of one title by one captain, two hours old. A third vessel id has no record.
        private static async Task<GroupingFixture> SeedGroupingFixtureAsync(DatabaseDriver database)
        {
            Vessel alphaVessel = await database.Vessels.CreateAsync(new Vessel("alpha-vessel", "https://github.com/test/alpha.git")).ConfigureAwait(false);
            Vessel bravoVessel = await database.Vessels.CreateAsync(new Vessel("bravo-vessel", "https://github.com/test/bravo.git")).ConfigureAwait(false);
            DateTime now = new DateTime(DateTime.UtcNow.Ticks - (DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            string alpha = alphaVessel.Id;
            string bravo = bravoVessel.Id;
            List<Papercut> papercuts = new List<Papercut>
            {
                BuildPapercut(alpha, PapercutCategoryEnum.ToolFailure, PapercutSeverityEnum.Low, "build step 1 times out", "cpt_1", "msn_a1", now.AddMinutes(-50)),
                BuildPapercut(alpha, PapercutCategoryEnum.ToolFailure, PapercutSeverityEnum.High, "build step 2 times out", "cpt_2", "msn_a2", now.AddMinutes(-40)),
                BuildPapercut(alpha, PapercutCategoryEnum.ToolFailure, PapercutSeverityEnum.Low, "build step 3 times out", "cpt_3", "msn_a3", now.AddMinutes(-20)),
                BuildPapercut(alpha, PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Medium, "README omits setup", "cpt_1", "msn_a4", now.AddMinutes(-30)),
                BuildPapercut(alpha, PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Low, "CHANGELOG omits setup", "cpt_1", "msn_a5", now.AddMinutes(-10)),
                BuildPapercut(bravo, PapercutCategoryEnum.TestFlake, PapercutSeverityEnum.Medium, "timing test flakes", "cpt_9", "msn_b1", now.AddHours(-2)),
                BuildPapercut(bravo, PapercutCategoryEnum.TestFlake, PapercutSeverityEnum.Low, "timing test flakes", "cpt_9", "msn_b2", now.AddHours(-2).AddMinutes(1)),
                BuildPapercut(bravo, PapercutCategoryEnum.TestFlake, PapercutSeverityEnum.Low, "timing test flakes", "cpt_9", "msn_b3", now.AddHours(-2).AddMinutes(2)),
                BuildPapercut("vsl_unregistered", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Low, "no guide", "cpt_4", "msn_c1", now.AddMinutes(-5))
            };
            foreach (Papercut papercut in papercuts)
                await database.Events.CreateAsync(PapercutService.ToEvent(papercut), CancellationToken.None).ConfigureAwait(false);
            return new GroupingFixture { AlphaId = alpha, BravoId = bravo, Now = now };
        }

        private static string AlphaCode(int value)
        {
            string result = String.Empty;
            do
            {
                result = (char)('a' + (value % 26)) + result;
                value = (value / 26) - 1;
            }
            while (value >= 0);
            return result;
        }
    }
}
