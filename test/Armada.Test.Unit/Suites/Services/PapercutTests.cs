namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

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
                original.Title = "sibling repo EcuLink was not provisioned";
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
    }
}
