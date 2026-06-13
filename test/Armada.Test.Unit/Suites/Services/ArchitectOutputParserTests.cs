namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Tests for ArchitectOutputParser: valid output, structural failures, cycle detection, blocked verdict, edge cases.</summary>
    public class ArchitectOutputParserTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Architect Output Parser";

        private static ArchitectOutputParser CreateSut() => new ArchitectOutputParser();

        private static string MakeMissionBlock(string id, string title, string preferredModel, string dependsOn = "", string description = "Do the thing.")
        {
            string dep = string.IsNullOrEmpty(dependsOn) ? "" : "\ndependsOnMissionId: " + dependsOn;
            return "[ARMADA:MISSION]\nid: " + id + "\ntitle: " + title + "\npreferredModel: " + preferredModel + dep + "\ndescription: " + description + "\n[ARMADA:MISSION-END]";
        }

        private static string MakePlanPrefix() =>
            "**Goal:** Build something great\n\n## File structure\n\n| File | Role |\n|---|---|\n\n## Task dispatch graph\n\nM1 -> M2\n\n";

        /// <summary>Builds <paramref name="count"/> dependency-free, structurally valid mission blocks joined by newlines.</summary>
        private static string MakeValidMissionBlocks(int count)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 1; i <= count; i++)
            {
                if (i > 1) sb.Append('\n');
                sb.Append(MakeMissionBlock("M" + i, "Mission " + i, "mid"));
            }
            return sb.ToString();
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Parse_ValidSingleMission_ReturnsValid", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string input = MakePlanPrefix() + MakeMissionBlock("M1", "Foundation", "mid");
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.Valid, result.Verdict, "Single valid block should return Valid");
                AssertEqual(0, result.Errors.Count, "No errors expected");
                AssertEqual(1, result.Missions.Count, "Exactly one mission expected");
                AssertNotNull(result.Plan, "Plan should be populated");
                AssertEqual("M1", result.Missions[0].Id, "Mission id should be M1");
                AssertEqual("Foundation", result.Missions[0].Title, "Mission title should match");
                AssertEqual("mid", result.Missions[0].PreferredModel, "PreferredModel should match");
                return Task.CompletedTask;
            });

            await RunTest("Parse_ValidThreeMissions_ReturnsValid", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string blocks =
                    MakeMissionBlock("M1", "First", "mid") + "\n" +
                    MakeMissionBlock("M2", "Second", "high", "M1") + "\n" +
                    MakeMissionBlock("M3", "Third", "low", "M2");
                string input = MakePlanPrefix() + blocks;
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.Valid, result.Verdict, "Three valid blocks should return Valid");
                AssertEqual(3, result.Missions.Count, "Three missions expected");
                AssertEqual(0, result.Errors.Count, "No errors expected");
                AssertEqual("M1", result.Missions[0].Id, "First mission id");
                AssertEqual("M2", result.Missions[1].Id, "Second mission id");
                AssertEqual("M3", result.Missions[2].Id, "Third mission id");
                AssertEqual("M1", result.Missions[1].DependsOnMissionAlias, "M2 should depend on M1");
                AssertEqual("M2", result.Missions[2].DependsOnMissionAlias, "M3 should depend on M2");
                return Task.CompletedTask;
            });

            await RunTest("Parse_MissingTitle_ReturnsStructuralFailure", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string block = "[ARMADA:MISSION]\nid: M1\npreferredModel: mid\ndescription: something\n[ARMADA:MISSION-END]";
                string input = MakePlanPrefix() + block;
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.StructuralFailure, result.Verdict, "Missing title should return StructuralFailure");
                AssertTrue(result.Errors.Count > 0, "Errors list should be non-empty");
                bool found = false;
                foreach (ArchitectParseError e in result.Errors)
                {
                    if (e.Type == "missing_field" && e.Field == "title" && e.MissionId == "M1")
                    {
                        found = true;
                        break;
                    }
                }
                AssertTrue(found, "Error should have type=missing_field, field=title, missionId=M1");
                return Task.CompletedTask;
            });

            await RunTest("Parse_BadIdShape_ReturnsStructuralFailure", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string block = "[ARMADA:MISSION]\nid: Mission1\ntitle: Bad ID\npreferredModel: claude-sonnet-4-6\ndescription: something\n[ARMADA:MISSION-END]";
                string input = MakePlanPrefix() + block;
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.StructuralFailure, result.Verdict, "Bad id shape should return StructuralFailure");
                bool found = false;
                foreach (ArchitectParseError e in result.Errors)
                {
                    if (e.Type == "bad_id_shape" && e.MissionId == "Mission1")
                    {
                        found = true;
                        break;
                    }
                }
                AssertTrue(found, "Error should have type=bad_id_shape for id 'Mission1'");
                return Task.CompletedTask;
            });

            await RunTest("Parse_NonTierPreferredModel_ReturnsStructuralFailure", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                // Concrete model names are no longer valid in Architect output; only low/mid/high are accepted.
                string block = "[ARMADA:MISSION]\nid: M1\ntitle: Test\npreferredModel: claude-sonnet-4-6\ndescription: something\n[ARMADA:MISSION-END]";
                string input = MakePlanPrefix() + block;
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.StructuralFailure, result.Verdict, "Concrete model name should return StructuralFailure in Architect output");
                bool found = false;
                foreach (ArchitectParseError e in result.Errors)
                {
                    if (e.Type == "invalid_tier" && e.MissionId == "M1" && e.Field == "preferredModel")
                    {
                        found = true;
                        break;
                    }
                }
                AssertTrue(found, "Error should have type=invalid_tier for concrete model name");
                return Task.CompletedTask;
            });

            await RunTest("Parse_AllThreeTierValues_AreValid", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string blocks =
                    MakeMissionBlock("M1", "Low tier", "low") + "\n" +
                    MakeMissionBlock("M2", "Mid tier", "mid", "M1") + "\n" +
                    MakeMissionBlock("M3", "High tier", "high", "M2");
                string input = MakePlanPrefix() + blocks;
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.Valid, result.Verdict, "low/mid/high should all be valid tier values");
                AssertEqual(0, result.Errors.Count, "No errors for valid tier values");
                return Task.CompletedTask;
            });

            await RunTest("Parse_TierAliases_AreValid", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string blocks =
                    MakeMissionBlock("M1", "Quick alias", "quick") + "\n" +
                    MakeMissionBlock("M2", "Medium alias", "medium", "M1");
                string input = MakePlanPrefix() + blocks;
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.Valid, result.Verdict, "quick and medium aliases should be valid");
                AssertEqual(0, result.Errors.Count, "No errors for tier aliases");
                return Task.CompletedTask;
            });

            await RunTest("Parse_DispatchGraphCycle_ReturnsStructuralFailure", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                // M1 depends on M2, M2 depends on M1 -> cycle
                string blocks =
                    MakeMissionBlock("M1", "First", "mid", "M2") + "\n" +
                    MakeMissionBlock("M2", "Second", "high", "M1");
                string input = MakePlanPrefix() + blocks;
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.StructuralFailure, result.Verdict, "Cycle should return StructuralFailure");
                bool found = false;
                foreach (ArchitectParseError e in result.Errors)
                {
                    if (e.Type == "dispatch_graph_cycle")
                    {
                        found = true;
                        break;
                    }
                }
                AssertTrue(found, "Error should have type=dispatch_graph_cycle");
                return Task.CompletedTask;
            });

            await RunTest("Parse_BlockedVerdict_ReturnsBlockedWithQuestions", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string input = "Some preamble\n[ARMADA:RESULT] BLOCKED\n- Q1: What is the target framework?\n- Q2: Should this be async?";
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.Blocked, result.Verdict, "BLOCKED marker should return Blocked verdict");
                AssertEqual(2, result.BlockedQuestions.Count, "Two blocked questions expected");
                AssertContains("Q1", result.BlockedQuestions[0], "First question should contain Q1");
                AssertContains("Q2", result.BlockedQuestions[1], "Second question should contain Q2");
                return Task.CompletedTask;
            });

            await RunTest("Parse_EmptyOutput_ReturnsStructuralFailure", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                ArchitectParseResult r1 = sut.Parse("");
                ArchitectParseResult r2 = sut.Parse("   ");

                AssertEqual(ArchitectParseVerdict.StructuralFailure, r1.Verdict, "Empty string should return StructuralFailure");
                AssertEqual("empty_output", r1.Errors[0].Type, "Error type should be empty_output for empty string");
                AssertEqual(ArchitectParseVerdict.StructuralFailure, r2.Verdict, "Whitespace-only string should return StructuralFailure");
                AssertEqual("empty_output", r2.Errors[0].Type, "Error type should be empty_output for whitespace");
                return Task.CompletedTask;
            });

            await RunTest("Parse_UnterminatedBlock_ReturnsStructuralFailure", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string input = MakePlanPrefix() + "[ARMADA:MISSION]\nid: M1\ntitle: Unterminated\npreferredModel: mid\ndescription: oops\n";
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.StructuralFailure, result.Verdict, "Unterminated block should return StructuralFailure");
                bool found = false;
                foreach (ArchitectParseError e in result.Errors)
                {
                    if (e.Type == "unterminated_block")
                    {
                        found = true;
                        break;
                    }
                }
                AssertTrue(found, "Error should have type=unterminated_block");
                return Task.CompletedTask;
            });

            await RunTest("Parse_NoMissionMarkers_ReturnsStructuralFailure", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string input = MakePlanPrefix() + "This is a valid markdown plan body with no mission markers at all.";
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.StructuralFailure, result.Verdict, "No mission markers should return StructuralFailure");
                bool found = false;
                foreach (ArchitectParseError e in result.Errors)
                {
                    if (e.Type == "no_mission_blocks")
                    {
                        found = true;
                        break;
                    }
                }
                AssertTrue(found, "Error should have type=no_mission_blocks");
                return Task.CompletedTask;
            });

            await RunTest("Parse_NineMissionsCapEight_ReturnsOverCap", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string input = MakePlanPrefix() + MakeValidMissionBlocks(9);
                ArchitectParseResult result = sut.Parse(input, 8);

                AssertEqual(ArchitectParseVerdict.OverCap, result.Verdict, "9 missions with cap 8 should return OverCap");
                AssertEqual(0, result.Errors.Count, "Well-formed over-cap output should have no structural errors");
                AssertEqual(9, result.Missions.Count, "All 9 missions should still be parsed");
                AssertEqual(9, result.MissionCount, "MissionCount should report the parsed count");
                AssertEqual(8, result.MaxMissions, "MaxMissions should echo the cap that was exceeded");
                AssertEqual(2, result.RecommendedSubVoyages, "ceil(9/8) == 2 recommended sub-voyages");
                return Task.CompletedTask;
            });

            await RunTest("Parse_AtCap_ReturnsValid", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string input = MakePlanPrefix() + MakeValidMissionBlocks(8);
                ArchitectParseResult result = sut.Parse(input, 8);

                AssertEqual(ArchitectParseVerdict.Valid, result.Verdict, "Exactly at cap (8 == 8) should return Valid, not OverCap");
                AssertEqual(8, result.Missions.Count, "All 8 missions should be parsed");
                AssertEqual(0, result.Errors.Count, "No errors expected at cap");
                AssertEqual(0, result.MissionCount, "MissionCount stays 0 when not OverCap");
                AssertEqual(0, result.MaxMissions, "MaxMissions stays 0 when not OverCap");
                AssertEqual(0, result.RecommendedSubVoyages, "RecommendedSubVoyages stays 0 when not OverCap");
                return Task.CompletedTask;
            });

            await RunTest("Parse_OverCapButMalformed_StillStructuralFailure", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                // 8 valid blocks plus one with a bad id shape -> 9 missions (over cap 8) AND a structural error.
                string badBlock = "[ARMADA:MISSION]\nid: Mission9\ntitle: Bad ID\npreferredModel: mid\ndescription: oops\n[ARMADA:MISSION-END]";
                string input = MakePlanPrefix() + MakeValidMissionBlocks(8) + "\n" + badBlock;
                ArchitectParseResult result = sut.Parse(input, 8);

                AssertEqual(ArchitectParseVerdict.StructuralFailure, result.Verdict, "Structural errors must win over OverCap");
                AssertTrue(result.Errors.Count > 0, "Errors list should be non-empty");
                AssertEqual(0, result.MissionCount, "MissionCount should not be set on the StructuralFailure path");
                AssertEqual(0, result.MaxMissions, "MaxMissions should not be set on the StructuralFailure path");
                AssertEqual(0, result.RecommendedSubVoyages, "RecommendedSubVoyages should not be set on the StructuralFailure path");
                return Task.CompletedTask;
            });

            await RunTest("Parse_NoCapOverload_DefaultsUnbounded", () =>
            {
                ArchitectOutputParser sut = CreateSut();
                string input = MakePlanPrefix() + MakeValidMissionBlocks(20);
                ArchitectParseResult result = sut.Parse(input);

                AssertEqual(ArchitectParseVerdict.Valid, result.Verdict, "Single-arg Parse is unbounded; 20 missions should stay Valid");
                AssertEqual(20, result.Missions.Count, "All 20 missions should be parsed");
                AssertEqual(0, result.Errors.Count, "No errors expected");
                AssertEqual(0, result.MissionCount, "MissionCount stays 0 on the unbounded path");
                return Task.CompletedTask;
            });
        }
    }
}
