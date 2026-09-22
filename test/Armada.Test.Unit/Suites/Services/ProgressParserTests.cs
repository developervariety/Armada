namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Common;

    public class ProgressParserTests : TestSuite
    {
        public override string Name => "Progress Parser";

        protected override async Task RunTestsAsync()
        {
            await RunTest("TryParse Null ReturnsNull", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse(null!);
                AssertNull(result);
            });

            await RunTest("TryParse Empty ReturnsNull", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("");
                AssertNull(result);
            });

            await RunTest("TryParse NoSignal ReturnsNull", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("Just a regular log line");
                AssertNull(result);
            });

            await RunTest("TryParse ProgressSignal ParsesPercentage", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] 75");
                AssertNotNull(result);
                AssertEqual("progress", result!.Type);
                AssertEqual("75", result.Value);
                AssertEqual(75, result.Percentage);
            });

            await RunTest("TryParse ProgressSignal WithPercentSign", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] 50%");
                AssertNotNull(result);
                AssertEqual(50, result!.Percentage);
            });

            await RunTest("TryParse ProgressSignal ClampsTo0", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] -10");
                AssertNotNull(result);
                AssertEqual(0, result!.Percentage);
            });

            await RunTest("TryParse ProgressSignal ClampsTo100", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] 150");
                AssertNotNull(result);
                AssertEqual(100, result!.Percentage);
            });

            await RunTest("TryParse StatusSignal ParsesEnum", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:STATUS] Testing");
                AssertNotNull(result);
                AssertEqual("status", result!.Type);
                AssertEqual(MissionStatusEnum.Testing, result.MissionStatus);
            });

            await RunTest("TryParse StatusSignal Review", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:STATUS] Review");
                AssertNotNull(result);
                AssertEqual(MissionStatusEnum.Review, result!.MissionStatus);
            });

            await RunTest("TryParse StatusSignal CaseInsensitive", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[armada:status] testing");
                AssertNotNull(result);
                AssertEqual("status", result!.Type);
                AssertEqual(MissionStatusEnum.Testing, result.MissionStatus);
            });

            await RunTest("TryParse StatusSignal InvalidEnum NoMissionStatus", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:STATUS] InvalidState");
                AssertNotNull(result);
                AssertEqual("status", result!.Type);
                AssertNull(result.MissionStatus);
            });

            await RunTest("TryParse MessageSignal ParsesValue", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:MESSAGE] Running unit tests now");
                AssertNotNull(result);
                AssertEqual("message", result!.Type);
                AssertEqual("Running unit tests now", result.Value);
                AssertNull(result.Percentage);
                AssertNull(result.MissionStatus);
            });

            await RunTest("TryParse ResultSignal PreservesValue", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:RESULT] COMPLETE");
                AssertNotNull(result);
                AssertEqual("result", result!.Type);
                AssertEqual("COMPLETE", result.Value);
                AssertNull(result.Percentage);
                AssertNull(result.MissionStatus);
            });

            await RunTest("TryParse VerdictSignal PreservesValue", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:VERDICT] PASS");
                AssertNotNull(result);
                AssertEqual("verdict", result!.Type);
                AssertEqual("PASS", result.Value);
                AssertNull(result.Percentage);
                AssertNull(result.MissionStatus);
            });

            await RunTest("TryParse StandaloneSignal WithWhitespace", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("  [ARMADA:PROGRESS] 30  ");
                AssertNotNull(result);
                AssertEqual(30, result!.Percentage);
            });

            await RunTest("TryParse EmbeddedInOutput ReturnsNull", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("some prefix [ARMADA:PROGRESS] 30");
                AssertNull(result);
            });

            await RunTest("TryParse InstructionExample ReturnsNull", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("- `[ARMADA:PROGRESS] 50` -- report completion percentage (0-100)");
                AssertNull(result);
            });

            await RunTest("TryParse MultiLineRecord FindsSignalOnOwnLine", () =>
            {
                // A runtime record is not always one physical line: a Codex agent_message or Claude
                // assistant text block can carry a marker followed by prose in a single record.
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse(
                    "[ARMADA:RESULT] COMPLETE\nWired the recovery rows, passed 25 focused tests, committed d152a300.");
                AssertNotNull(result);
                AssertEqual("result", result!.Type);
                AssertEqual("COMPLETE", result.Value);
            });

            await RunTest("ParseAll MultiLineRecord ReturnsEveryMarkerInOrder", () =>
            {
                // A final answer that opens with a papercut and ends with RESULT carries both, and
                // each must reach its own consumer.
                List<ProgressParser.ProgressSignal> signals = ProgressParser.ParseAll(
                    "[ARMADA:PAPERCUT] {\"category\":\"RepoFriction\",\"severity\":\"Low\",\"title\":\"Worker branch lagged\"}\n\n[ARMADA:RESULT] COMPLETE\nWired the rows.");
                AssertEqual(2, signals.Count);
                AssertEqual("papercut", signals[0].Type);
                AssertEqual("result", signals[1].Type);
                AssertEqual("COMPLETE", signals[1].Value);
            });

            await RunTest("ParseAll MessageBeforeResult ReturnsBoth", () =>
            {
                List<ProgressParser.ProgressSignal> signals = ProgressParser.ParseAll(
                    "[ARMADA:MESSAGE] Ran the suite\n[ARMADA:RESULT] COMPLETE");
                AssertEqual(2, signals.Count);
                AssertEqual("message", signals[0].Type);
                AssertEqual("result", signals[1].Type);

                ProgressParser.ProgressSignal? first = ProgressParser.TryParse(
                    "[ARMADA:MESSAGE] Ran the suite\n[ARMADA:RESULT] COMPLETE");
                AssertEqual("message", first!.Type, "TryParse returns the first marker of the record");
            });

            await RunTest("ParseAll NoMarker ReturnsEmpty", () =>
            {
                AssertEqual(0, ProgressParser.ParseAll(null).Count);
                AssertEqual(0, ProgressParser.ParseAll("Some prefix text [ARMADA:PROGRESS] 30\ncontinued prose").Count);
            });

            await RunTest("TryParse MultiLineRecord ProsePrefixStillNull", () =>
            {
                // A marker in the MIDDLE of a prose line is not a signal, in a single-line or a
                // multi-line record alike: an instruction file documents the format this way.
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse(
                    "Some prefix text [ARMADA:PROGRESS] 30\ncontinued prose");
                AssertNull(result);
            });

            await RunTest("TryParse MultiLineRecord BlankAndProseOnlyNull", () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse(
                    "Wired the recovery rows into TryGet.\n\nPassed 25 focused tests.");
                AssertNull(result);
            });
        }
    }
}
