namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    public class ProgressParserTests : TestSuite
    {
        public override string Name => "Progress Parser";

        protected override async Task RunTestsAsync()
        {
            // One rule for every marker reader: a marker counts only at the start of a line, leading whitespace
            // allowed. The runtimes put a real marker on its own line (each text block is its own record, and
            // streamed text is joined into whole lines before a record is written), so a marker glued to other
            // text is never a real one.
            await RunTest("Every marker reader counts a marker only at the start of a line", () =>
            {
                string[] notMarkers =
                {
                    "The brief says to finish with [ARMADA:RESULT] COMPLETE once done.",
                    "Remember that a Judge writes [ARMADA:VERDICT] PASS at the end.",
                    "Done.[ARMADA:RESULT] COMPLETE"
                };
                foreach (string prose in notMarkers)
                {
                    AssertFalse(ProgressParser.HasTerminalMarker(prose), "ProgressParser: " + prose);
                    AssertFalse(MissionService.HasCompletionMarker(prose), "HasCompletionMarker: " + prose);
                    AssertEqual(CaptainRefusalKindEnum.ModelPolicyRefusal, CaptainRefusalClassifier.Classify(prose + "\nI can't help with that.").Kind,
                        "A completion marker mid-line does not suppress the refusal fallback: " + prose);
                }

                string refusalProse = "The captain may write [ARMADA:RESULT] REFUSED out of scope when it declines.";
                AssertFalse(CaptainRefusalClassifier.HasRefusalMarker(refusalProse), "A refusal marker mid-line is not a marker");
                AssertTrue(CaptainRefusalClassifier.Classify(refusalProse).Kind != CaptainRefusalKindEnum.DeclaredRefusal, "Classify ignores a refusal marker mid-line");
                AssertFalse(ProgressParser.HasSignal("Note: [ARMADA:RESULT] COMPLETE is the claim.", "result", "verdict"), "The handoff marker check ignores a marker mid-line");
                ArchitectParseResult architectProse = new ArchitectOutputParser().Parse("Plan: if stuck write [ARMADA:RESULT] BLOCKED and list questions.\n- q1");
                AssertTrue(architectProse.Verdict != ArchitectParseVerdict.Blocked, "Architect BLOCKED mid-line is not a marker");
                Mission decorated = new Mission("m", "d") { Status = MissionStatusEnum.Complete, AgentOutput = "**[ARMADA:VERDICT] NEEDS_REVISION**" };
                AssertEqual("PASS", JudgeOutputParser.ParseVerdictLabel(decorated), "A decorated verdict marker does not start its line");

                string[] markers =
                {
                    "All checks pass.\n[ARMADA:RESULT] COMPLETE\nSummary follows.",
                    "Review body\n  [ARMADA:VERDICT] PASS"
                };
                foreach (string output in markers)
                {
                    AssertTrue(ProgressParser.HasTerminalMarker(output), "ProgressParser: " + output);
                    AssertTrue(MissionService.HasCompletionMarker(output), "HasCompletionMarker: " + output);
                    AssertTrue(ProgressParser.HasSignal(output, "result", "verdict"), "Handoff marker check: " + output);
                    AssertEqual(CaptainRefusalKindEnum.None, CaptainRefusalClassifier.Classify(output + "\nI can't help with that.").Kind, "A completion claim at line start suppresses the prose refusal fallback");
                }

                string refusal = "Looked at the brief.\n[ARMADA:RESULT] REFUSED out of scope";
                AssertTrue(CaptainRefusalClassifier.HasRefusalMarker(refusal), "A refusal marker at line start is a marker");
                CaptainRefusal declared = CaptainRefusalClassifier.Classify(refusal);
                AssertEqual(CaptainRefusalKindEnum.DeclaredRefusal, declared.Kind, "Classify reads a refusal marker at line start");
                AssertEqual("out of scope", declared.Reason, "The refusal reason follows the marker");
                ArchitectParseResult blocked = new ArchitectOutputParser().Parse("Plan draft\n[ARMADA:RESULT] BLOCKED\n- which database?");
                AssertEqual(ArchitectParseVerdict.Blocked, blocked.Verdict, "Architect BLOCKED at line start is a marker");
                AssertEqual("which database?", blocked.BlockedQuestions[0], "Blocked questions follow the marker line");
                Mission verdict = new Mission("m", "d") { Status = MissionStatusEnum.Complete, AgentOutput = "Findings\n[ARMADA:VERDICT] NEEDS_REVISION" };
                AssertEqual("NEEDS_REVISION", JudgeOutputParser.ParseVerdictLabel(verdict), "A verdict marker at line start is read");
            });

            // A stage is blocked only when its FINAL result or verdict is [ARMADA:RESULT] BLOCKED at the start of a
            // line. Every stage and the Architect parser read it through CaptainBlockedResult.
            await RunTest("The blocked-result rule reads only a final BLOCKED result at the start of a line", () =>
            {
                string[] notBlocked =
                {
                    "If you are stuck, end with [ARMADA:RESULT] BLOCKED and the question.\n[ARMADA:RESULT] COMPLETE",
                    "Done.[ARMADA:RESULT] BLOCKED",
                    "**[ARMADA:RESULT] BLOCKED**",
                    "[ARMADA:RESULT] BLOCKED\n- which database?\nResolved from the design doc.\n[ARMADA:RESULT] COMPLETE",
                    "[ARMADA:RESULT] BLOCKED\nAnswered by the brief after all.\n[ARMADA:VERDICT] PASS",
                    "[ARMADA:RESULT] BLOCKEDNESS noted",
                    "**BLOCKED** - the status tool is missing.\n[ARMADA:RESULT] COMPLETE"
                };
                foreach (string output in notBlocked)
                {
                    AssertFalse(CaptainBlockedResult.IsBlocked(output), "Not blocked: " + output);
                    AssertTrue(new ArchitectOutputParser().Parse(output).Verdict != ArchitectParseVerdict.Blocked, "Architect agrees, not blocked: " + output);
                }

                AssertTrue(CaptainBlockedResult.TryRead("Plan draft\n[ARMADA:RESULT] BLOCKED\n- which database?", out string after), "A final BLOCKED at line start is blocked");
                AssertEqual("- which database?", after, "The question after the marker is read");
                AssertTrue(CaptainBlockedResult.TryRead("[ARMADA:VERDICT] PASS\n  [ARMADA:RESULT] BLOCKED: which schema is live?", out string inline), "A BLOCKED after a verdict is the final outcome");
                AssertEqual("which schema is live?", inline, "The question on the marker line is read");
                AssertTrue(CaptainBlockedResult.TryRead("Context line.\nWhich retention window applies?\n[ARMADA:RESULT] BLOCKED", out string before), "BLOCKED as the last line is blocked");
                AssertContains("Which retention window applies?", before, "With nothing after the marker, the lines above it are the question");
                AssertTrue(CaptainBlockedResult.IsBlockedFailure(CaptainBlockedResult.BuildFailureReason("Judge", before)), "The failure reason carries the blocked class");
            });

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
