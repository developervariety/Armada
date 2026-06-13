namespace Armada.Test.Unit.Suites.Models
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for <see cref="ContextPackUsageSummary.FromEventPayload"/>.
    /// </summary>
    public class ContextPackUsageSummaryTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Context Pack Usage Summary";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FromEventPayload_ReadBeforeSearch_MapsCountsAndOffsets", async () =>
            {
                string payload = BuildEmitterPayload(
                    compliance: "ReadBeforeSearch",
                    logAvailable: true,
                    packStaged: true,
                    firstPackRead: 120,
                    firstSearch: 450,
                    searchCount: 2,
                    readFromPack: new List<string> { "src/Foo.cs", "src/Bar.cs" },
                    ignoredFromPack: new List<string> { "src/Unused.cs" },
                    grepDiscovered: new List<string>(),
                    edited: new List<string> { "src/Foo.cs" });

                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary, "summary should parse");
                AssertEqual("ReadBeforeSearch", summary!.Compliance);
                AssertTrue(summary.PackStaged, "PackStaged");
                AssertTrue(summary.LogAvailable, "LogAvailable");
                AssertEqual(2, summary.SearchToolCallCount);
                AssertEqual(120, summary.FirstContextPackReadOffset);
                AssertEqual(450, summary.FirstSearchToolOffset);
                AssertEqual(2, summary.FilesReadFromPackCount);
                AssertEqual(1, summary.FilesIgnoredFromPackCount);
                AssertEqual(0, summary.FilesGrepDiscoveredCount);
                AssertEqual(1, summary.FilesEditedCount);
            });

            await RunTest("FromEventPayload_SearchWithoutPackRead_MapsGrepDiscovered", async () =>
            {
                string payload = BuildEmitterPayload(
                    compliance: "SearchWithoutPackRead",
                    logAvailable: true,
                    packStaged: true,
                    firstPackRead: null,
                    firstSearch: 80,
                    searchCount: 3,
                    readFromPack: new List<string>(),
                    ignoredFromPack: new List<string> { "src/Ignored.cs" },
                    grepDiscovered: new List<string> { "src/Found.cs", "src/Other.cs" },
                    edited: new List<string>());

                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary, "summary should parse");
                AssertEqual("SearchWithoutPackRead", summary!.Compliance);
                AssertEqual(3, summary.SearchToolCallCount);
                AssertNull(summary.FirstContextPackReadOffset, "no pack read");
                AssertEqual(80, summary.FirstSearchToolOffset);
                AssertEqual(0, summary.FilesReadFromPackCount);
                AssertEqual(1, summary.FilesIgnoredFromPackCount);
                AssertEqual(2, summary.FilesGrepDiscoveredCount);
                AssertEqual(0, summary.FilesEditedCount);
            });

            await RunTest("FromEventPayload_NoPackStagedNoSearch_MapsPackStagedFalse", async () =>
            {
                string payload = BuildEmitterPayload(
                    compliance: "NoPackStagedNoSearch",
                    logAvailable: true,
                    packStaged: false,
                    firstPackRead: null,
                    firstSearch: null,
                    searchCount: 0,
                    readFromPack: new List<string>(),
                    ignoredFromPack: new List<string>(),
                    grepDiscovered: new List<string>(),
                    edited: new List<string>());

                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary, "summary should parse");
                AssertEqual("NoPackStagedNoSearch", summary!.Compliance);
                AssertFalse(summary.PackStaged, "PackStaged false");
                AssertEqual(0, summary.SearchToolCallCount);
                AssertEqual(0, summary.FilesReadFromPackCount);
                AssertEqual(0, summary.FilesIgnoredFromPackCount);
                AssertEqual(0, summary.FilesGrepDiscoveredCount);
                AssertEqual(0, summary.FilesEditedCount);
            });

            await RunTest("FromEventPayload_LogUnavailable_MapsLogAvailableFalse", async () =>
            {
                string payload = BuildEmitterPayload(
                    compliance: "LogUnavailablePackStaged",
                    logAvailable: false,
                    packStaged: true,
                    firstPackRead: null,
                    firstSearch: null,
                    searchCount: 0,
                    readFromPack: new List<string>(),
                    ignoredFromPack: new List<string>(),
                    grepDiscovered: new List<string>(),
                    edited: new List<string>());

                ContextPackUsageSummary? summary = ContextPackUsageSummary.FromEventPayload(payload);
                AssertNotNull(summary, "summary should parse");
                AssertEqual("LogUnavailablePackStaged", summary!.Compliance);
                AssertFalse(summary.LogAvailable, "LogAvailable false");
                AssertTrue(summary.PackStaged, "PackStaged true");
            });

            await RunTest("FromEventPayload_Null_ReturnsNull", async () =>
            {
                AssertNull(ContextPackUsageSummary.FromEventPayload(null), "null payload");
            });

            await RunTest("FromEventPayload_EmptyOrWhitespace_ReturnsNull", async () =>
            {
                AssertNull(ContextPackUsageSummary.FromEventPayload(""), "empty");
                AssertNull(ContextPackUsageSummary.FromEventPayload("   "), "whitespace");
            });

            await RunTest("FromEventPayload_MalformedJson_ReturnsNull", async () =>
            {
                AssertNull(ContextPackUsageSummary.FromEventPayload("{not json"), "malformed");
            });

            await RunTest("EventType_MatchesEmitterConstant", async () =>
            {
                AssertEqual("mission.context_pack_usage", ContextPackUsageSummary.EventType);
            });
        }

        private static string BuildEmitterPayload(
            string compliance,
            bool logAvailable,
            bool packStaged,
            int? firstPackRead,
            int? firstSearch,
            int searchCount,
            List<string> readFromPack,
            List<string> ignoredFromPack,
            List<string> grepDiscovered,
            List<string> edited)
        {
            return JsonSerializer.Serialize(new
            {
                MissionId = "msn_test",
                LogAvailable = logAvailable,
                ContextPackStaged = packStaged,
                ContextPackCompliance = compliance,
                FirstContextPackReadOffset = firstPackRead,
                FirstSearchToolOffset = firstSearch,
                SearchToolCallCount = searchCount,
                FilesReadFromPack = readFromPack,
                FilesIgnoredFromPack = ignoredFromPack,
                FilesGrepDiscovered = grepDiscovered,
                FilesEdited = edited
            });
        }
    }
}
