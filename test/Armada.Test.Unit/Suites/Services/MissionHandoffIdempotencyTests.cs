namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core;
    using Armada.Core.Context;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Covers the pipeline-handoff idempotency guards. A handoff that runs twice for the same upstream
    /// mission (batch path plus the lazy self-heal path, or a rescue re-prepare) previously appended the
    /// whole prior-stage block a second time, so the persona preamble and the prior-stage block could
    /// each appear more than once in a captain brief.
    /// </summary>
    public class MissionHandoffIdempotencyTests : TestSuite
    {
        public override string Name => "Mission Handoff Idempotency";

        private const string _UpstreamId = "msn_upstream_one";
        private const string _OtherUpstreamId = "msn_upstream_two";

        private const string _Preamble =
            "## Your Role: TestEngineer (Write Tests)\n\nYou are writing tests for code changes made by the Worker.\n\n";

        /// <summary>
        /// Composes a description exactly as the handoff does: strip any prior block for this upstream,
        /// shrink every older block that remains, prepend the persona preamble only when absent, then
        /// append a fresh block. The compaction step must stay here, or this helper stops matching the
        /// production path and the suite starts proving behaviour that no longer exists.
        /// </summary>
        private string ApplyHandoff(string description, string upstreamId, string blockBody)
        {
            string stripped = MissionService.CompactOlderHandoffBlocks(
                MissionService.StripHandoffBlock(description, upstreamId));
            string block = "\n\n---\n" + MissionService.BuildHandoffMarker(upstreamId) + "\n" + blockBody;

            return MissionService.ContainsPersonaPreamble(stripped, _Preamble)
                ? stripped + block
                : _Preamble + stripped + block;
        }

        /// <summary>
        /// Builds a prior-stage block body of realistic size. A real block carries agent output and a
        /// diff and runs to thousands of characters. A toy body is smaller than the compact reference
        /// that would replace it, so compaction correctly leaves it alone and the test proves nothing.
        /// </summary>
        private string LargeBody(string tag)
        {
            return "## Prior Stage Output\n" + tag + "\n" +
                "### Diff from prior stage\n```diff\n" + new string('+', 4000) + "\n```\n";
        }

        private int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                index = haystack.IndexOf(needle, index, StringComparison.Ordinal);
                if (index < 0) break;
                count++;
                index += needle.Length;
            }
            return count;
        }

        private string ReportReference(string missionId)
        {
            return ReportReference("Judge", missionId);
        }

        private string ReportReference(string persona, string missionId)
        {
            Mission mission = new Mission
            {
                Id = missionId,
                AgentOutput = "complete judge output",
                Status = MissionStatusEnum.Complete
            };
            MissionOutputArtifactPage artifact = MissionOutputArtifact.Build(mission);
            return StageReportEssentials.Build(persona, missionId, null, artifact);
        }

        private bool HasCompleteDigest(string text)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                text,
                @"UTF-8 SHA-256 [0-9a-f]{64}",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        private string BuildRescueDescription(string kind, string missionId, out Mission failedMission)
        {
            failedMission = new Mission
            {
                Id = missionId,
                Title = "Recover the failed stage",
                Status = MissionStatusEnum.Failed,
                Description = "## Acceptance Criteria\n- " + new string('c', 1200) + "\n"
            };
            if (kind == "judge")
            {
                failedMission.Persona = "Judge";
                failedMission.AgentOutput = "## Correctness\n- The reviewed result misses a required case.\n## Verdict\nNEEDS_REVISION";
            }
            else if (kind == "worker")
            {
                failedMission.Persona = "Worker";
                failedMission.AgentOutput = "The worker stage failed after producing partial work.";
                failedMission.ReviewComment = "## Failure Modes\n- The retry path is not handled.";
            }
            else if (kind == "incomplete")
            {
                failedMission.Persona = "Judge";
                failedMission.AgentOutput = MissionOutputArtifact.StreamTruncationMarker
                    + "\n## Correctness\n- The captured output has a protected finding.";
                failedMission.ReviewComment = "## Correctness\n- The captured output has a protected finding.";
            }
            else
            {
                failedMission.Persona = "Judge";
                failedMission.ReviewComment = "## Correctness\n- Fallback review text requires another source check.";
            }

            Incident incident = new Incident
            {
                Id = "inc_test",
                Title = "Rescue reference test",
                Summary = "Verify the failed stage evidence remains reachable.",
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.Medium
            };
            return AutonomousRecoveryOrchestrator.BuildRescueDescription(failedMission, incident, 1);
        }

        private string RescueReferencePair(string rescueDescription, string missionId)
        {
            string[] lines = rescueDescription.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (lines[i].Contains("mission-output:" + missionId, StringComparison.Ordinal))
                    return lines[i].TrimEnd('\r') + "\n" + lines[i + 1].TrimEnd('\r');
            }
            throw new InvalidOperationException("The production rescue description has no reference for " + missionId);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Repeated handoff for the same upstream produces one prior-stage block", async () =>
            {
                string first = ApplyHandoff("Base brief.", _UpstreamId, "## Prior Stage Output\nrun one\n");
                string second = ApplyHandoff(first, _UpstreamId, "## Prior Stage Output\nrun two\n");

                AssertEqual(1, CountOccurrences(second, MissionService.BuildHandoffMarker(_UpstreamId)), "handoff marker must appear once");
                AssertEqual(1, CountOccurrences(second, "## Prior Stage Output"), "prior-stage heading must appear once");
                AssertEqual(1, CountOccurrences(second, "## Your Role: TestEngineer (Write Tests)"), "persona preamble must appear once");
                AssertContains("run two", second, "the newest block must win");
                AssertFalse(second.Contains("run one", StringComparison.Ordinal), "the superseded block must be gone");
                AssertContains("Base brief.", second, "the base brief must survive");

                await Task.CompletedTask;
            });

            await RunTest("Handoff from a second upstream compacts the older block and keeps the newest in full", async () =>
            {
                string first = ApplyHandoff("Base brief.", _UpstreamId, LargeBody("from one"));
                string both = ApplyHandoff(first, _OtherUpstreamId, LargeBody("from two"));

                AssertEqual(1, CountOccurrences(both, MissionService.BuildHandoffMarker(_UpstreamId)), "first upstream must still be referenced");
                AssertEqual(1, CountOccurrences(both, MissionService.BuildHandoffMarker(_OtherUpstreamId)), "second upstream block must be added");

                // The older stage keeps a reference, not its body. Its output is on its branch, where
                // reading it costs the captain nothing until it decides it needs it.
                AssertContains("## Prior Stage (compacted)", both, "the older block must be reduced to a reference");
                AssertContains(_UpstreamId, both, "the reference must still name the upstream mission");
                AssertFalse(both.Contains("from one", StringComparison.Ordinal), "the older block body must not be carried");

                AssertContains("from two", both, "the newest block must be carried in full");
                AssertContains("Base brief.", both, "the base brief must survive");

                await Task.CompletedTask;
            });

            await RunTest("Compaction is idempotent and leaves a description with no handoff block alone", () =>
            {
                string plain = "Base brief with no handoff block.";
                AssertEqual(plain, MissionService.CompactOlderHandoffBlocks(plain), "a description with no block must be untouched");
                AssertEqual("", MissionService.CompactOlderHandoffBlocks(""), "an empty description must stay empty");
                AssertEqual("", MissionService.CompactOlderHandoffBlocks(null), "a null description must become empty");

                string withBlock = "Base brief." +
                    "\n\n---\n" + MissionService.BuildHandoffMarker(_UpstreamId) + "\n" +
                    "## Prior Stage Output\nThe previous pipeline stage (Worker) completed mission x.\n" +
                    "Branch: armada/example/msn_upstream_one\n" +
                    "### Diff from prior stage\n```diff\n" + new string('+', 4000) + "\n```\n";

                string once = MissionService.CompactOlderHandoffBlocks(withBlock);
                string twice = MissionService.CompactOlderHandoffBlocks(once);

                AssertEqual(once, twice, "compacting an already-compact description must change nothing");
                AssertContains("Branch: armada/example/msn_upstream_one", once, "the branch must survive so the work can still be found");
                AssertContains("Stage: Worker", once, "the upstream persona must survive");
                AssertFalse(once.Contains(new string('+', 4000), StringComparison.Ordinal), "the diff body must be dropped");
                AssertTrue(once.Length < withBlock.Length, "compaction must make the description smaller");

                // A stage that produced almost nothing leaves a block shorter than the reference would be.
                // Replacing it would add bytes to save bytes, so the original is kept.
                string tinyBlock = "Base brief." +
                    "\n\n---\n" + MissionService.BuildHandoffMarker(_UpstreamId) + "\nok\n";

                AssertEqual(tinyBlock, MissionService.CompactOlderHandoffBlocks(tinyBlock),
                    "a block already smaller than its reference must be left alone");
            });

            await RunTest("Replacing the first of two blocks leaves the later block intact", async () =>
            {
                string first = ApplyHandoff("Base brief.", _UpstreamId, LargeBody("from one"));
                string both = ApplyHandoff(first, _OtherUpstreamId, LargeBody("from two"));
                string replaced = ApplyHandoff(both, _UpstreamId, LargeBody("from one again"));

                AssertEqual(1, CountOccurrences(replaced, MissionService.BuildHandoffMarker(_UpstreamId)), "replaced marker must appear once");
                AssertEqual(1, CountOccurrences(replaced, MissionService.BuildHandoffMarker(_OtherUpstreamId)), "the other upstream must still be referenced");
                AssertContains("from one again", replaced, "the newest block must be carried in full");
                AssertFalse(replaced.Contains("from one\n", StringComparison.Ordinal), "the superseded first block must be gone");

                // The other upstream is now the older stage, so it is reduced to a reference in turn.
                // Which block stays full follows recency, not the order the upstreams arrived in.
                AssertContains("## Prior Stage (compacted)", replaced, "the now-older block must be reduced to a reference");
                AssertFalse(replaced.Contains("from two", StringComparison.Ordinal), "the now-older block body must not be carried");

                await Task.CompletedTask;
            });

            await RunTest("StripHandoffBlock leaves a description without the marker unchanged", async () =>
            {
                string description = "Base brief with no handoff block.";
                AssertEqual(description, MissionService.StripHandoffBlock(description, _UpstreamId), "description must be untouched");
                AssertEqual("", MissionService.StripHandoffBlock("", _UpstreamId), "empty description must stay empty");
                AssertEqual("", MissionService.StripHandoffBlock(null, _UpstreamId), "null description must become empty");
                AssertEqual(description, MissionService.StripHandoffBlock(description, ""), "an empty upstream id must strip nothing");

                await Task.CompletedTask;
            });

            await RunTest("ContainsPersonaPreamble matches on the heading line only", async () =>
            {
                AssertTrue(MissionService.ContainsPersonaPreamble(_Preamble + "rest", _Preamble), "identical preamble must match");
                AssertTrue(
                    MissionService.ContainsPersonaPreamble("## Your Role: TestEngineer (Write Tests)\n\nreworded body\n", _Preamble),
                    "a reworded body must still match on the heading");
                AssertFalse(MissionService.ContainsPersonaPreamble("Base brief.", _Preamble), "unrelated description must not match");
                AssertFalse(MissionService.ContainsPersonaPreamble(null, _Preamble), "null description must not match");
                AssertFalse(MissionService.ContainsPersonaPreamble(_Preamble, null), "null preamble must not match");

                await Task.CompletedTask;
            });

            await RunTest("TruncateMissionDescription bounds an over-budget brief and notes the cut", async () =>
            {
                string oversized = new string('x', 500);
                string bounded = MissionService.TruncateMissionDescription(oversized, 200);

                AssertTrue(bounded.Length <= 200, "bounded description must fit the budget");
                AssertContains("truncated to fit the budget", bounded, "the cut must be visible to the captain");

                string small = "short brief";
                AssertEqual(small, MissionService.TruncateMissionDescription(small, 200), "a brief under budget must be unchanged");
                AssertEqual("", MissionService.TruncateMissionDescription("", 200), "an empty brief must stay empty");

                await Task.CompletedTask;
            });

            await RunTest("TruncateMissionDescription keeps the newest handoff block, not just the head", async () =>
            {
                // A tail-only cut drops the newest prior-stage block, which is exactly the content the
                // downstream reviewing stage needs. The head+tail elision must keep both ends.
                string head = "## Mission brief\nBase scope that the whole voyage depends on.\n";
                string middle = new string('m', 2000);
                string tail = "\n\n---\n" + MissionService.BuildHandoffMarker(_OtherUpstreamId) + "\n" +
                    "## Prior Stage Output\n### Diff from prior stage\n```diff\n+the newest diff the judge must see\n```\n";

                string full = head + middle + tail;
                string bounded = MissionService.TruncateMissionDescription(full, 800);

                AssertTrue(bounded.Length <= 800, "bounded description must fit the budget");
                AssertContains("Base scope that the whole voyage depends on", bounded, "the head brief must survive");
                AssertContains("the newest diff the judge must see", bounded, "the newest handoff block must survive the cut");
                AssertContains(MissionService.BuildHandoffMarker(_OtherUpstreamId), bounded, "the newest handoff marker must survive");

                await Task.CompletedTask;
            });

            await RunTest("TruncateMissionDescription pins the acceptance-criteria block", async () =>
            {
                string head = "## Mission brief\nBase scope.\n";
                string criteria = "\n## Acceptance Criteria\n- Gate stays green\n- CHANGELOG names the behaviour\n";
                string middle = new string('m', 1500);
                string after = new string('n', 1500);
                string tail = "\n\n---\n" + MissionService.BuildHandoffMarker(_OtherUpstreamId) + "\n" +
                    "## Prior Stage Output\n### Diff from prior stage\n```diff\n+the newest diff the judge must see\n```\n";
                string full = head + middle + criteria + after + tail;
                string bounded = MissionService.TruncateMissionDescription(full, 900);

                AssertTrue(bounded.Length <= 900, "bounded description must fit the budget");
                AssertContains("## Acceptance Criteria", bounded, "the criteria heading must survive the cut");
                AssertContains("Gate stays green", bounded, "each criterion must survive the cut");
                AssertContains("CHANGELOG names the behaviour", bounded, "each criterion must survive the cut");
                AssertContains("the newest diff the judge must see", bounded, "the newest handoff block must still survive");

                await Task.CompletedTask;
            });

            await RunTest("Truncation preserves every criterion when its heading survives in the head", async () =>
            {
                string criteria = "## Acceptance Criteria\n- " + new string('a', 90) + "\n- " + new string('b', 90) + "\n- Final required behavior\n";
                string full = criteria + "## Scope\n" + new string('x', 5000) + "\n## Prior Stage Output\nLatest diff.\n";
                for (int budget = 600; budget <= 1600; budget += 100)
                {
                    string bounded = MissionService.TruncateMissionDescription(full, budget);
                    AssertEqual(3, JudgeAcceptanceWalk.ExtractCriteria(bounded).Count);
                    AssertContains("Final required behavior", bounded);
                    AssertTrue(bounded.Length <= budget);
                }
                await Task.CompletedTask;
            });

            await RunTest("Metadata and total-budget trimming preserve criteria in the middle", async () =>
            {
                string criteria = "\n## Acceptance Criteria\n- Required behavior one\n- Required behavior two\n## Scope\n";
                string full = "## Mission\n" + new string('x', 14000) + criteria + new string('y', 14000) + "\nLatest output.\n";
                string bounded = MissionService.BoundMetadataDescription(full);
                AssertEqual(2, JudgeAcceptanceWalk.ExtractCriteria(bounded).Count);
                PromptModuleLedger ledger = new PromptModuleLedger();
                string content = ledger.Track("mission.metadata", full);
                string shrunk = MissionService.EnforceTotalBriefBudget(content, ledger, 3000, full);
                AssertEqual(2, JudgeAcceptanceWalk.ExtractCriteria(shrunk).Count);
                await Task.CompletedTask;
            });

            await RunTest("Mission truncation keeps the report artifact reference after large criteria, diff, and board notes", async () =>
            {
                string criteria = "\n## Acceptance Criteria\n" + String.Join("\n", Enumerable.Range(0, 10)
                    .Select(index => "- Required behavior " + index + ": " + new string((char)('a' + index), 1200))) + "\n";
                string artifact = ReportReference("msn_judge");
                string diff = "\n### Diff from prior stage\n```diff\n" + new string('+', 9000) + "\n```\n";
                string reportBody = "\n### Verdict\nNEEDS_REVISION\n" + new string('r', 3000) + "\n";
                string boardNotes = "\n## Voyage Board Notes\n" + String.Join("\n", Enumerable.Range(0, 10)
                    .Select(index => "- Note " + index + ": " + new string('n', 500))) + "\n";
                string full = "## Mission brief\nRead the prior Judge report and fix every finding.\n"
                    + criteria + "\n## Scope\n" + new string('s', 500) + "\n"
                    + "---\n" + MissionService.BuildHandoffMarker("msn_judge") + "\n## Prior Stage Output\n"
                    + diff + "\n" + artifact + reportBody + boardNotes;

                string bounded = MissionService.TruncateMissionDescription(full, 20000, "armada/brief-test");

                AssertContains("## Acceptance Criteria", bounded, "all criteria remain pinned");
                AssertContains("mission-output:msn_judge", bounded, "the report artifact remains readable");
                AssertTrue(HasCompleteDigest(bounded), "the full digest remains available for verification");
                AssertTrue(bounded.Length <= 20000, "the bounded description fits its limit");
                await Task.CompletedTask;
            });

            await RunTest("Metadata cap pins the artifact reference with and without criteria, but not the report body", async () =>
            {
                string[] builtInPersonas =
                {
                    "Judge",
                    PersonaCatalog.TestEngineer,
                    PersonaCatalog.ProductManager,
                    PersonaCatalog.UsabilityEngineer
                };
                foreach (string persona in builtInPersonas)
                {
                    foreach (bool withCriteria in new[] { false, true })
                    {
                        string criteria = withCriteria
                            ? "\n## Acceptance Criteria\n- Every finding is fixed with evidence.\n"
                            : String.Empty;
                        string artifact = ReportReference(persona, "msn_judge")
                            + "Disposable report narrative: " + new string('r', 3000) + "\n";
                        string full = "## Mission brief\n" + new string('h', 5000) + "\n"
                            + criteria + artifact + new string('m', 12000) + "\n## Latest handoff\n" + new string('t', 3000);

                        string bounded = MissionService.BoundMetadataDescription(full);

                        AssertTrue(bounded.Length <= MissionService._MaxMetadataDescriptionChars, persona + " metadata stays within its cap");
                        AssertContains("### Report essentials (" + persona + " msn_judge)", bounded, persona + " heading remains readable");
                        AssertContains("mission-output:msn_judge", bounded, persona + " artifact reference remains readable");
                        AssertTrue(HasCompleteDigest(bounded), persona + " full digest remains available");
                        AssertFalse(bounded.Contains(new string('r', 3000), StringComparison.Ordinal), persona + " report body is not pinned with the reference");
                        if (withCriteria)
                            AssertContains("Every finding is fixed with evidence", bounded, persona + " acceptance criteria remain pinned with the report reference");
                    }
                }
                await Task.CompletedTask;
            });

            await RunTest("Total-budget shrinking keeps the report artifact reference", async () =>
            {
                string artifact = ReportReference("msn_judge");
                string description = "## Mission brief\n" + new string('h', 5000) + "\n"
                    + artifact + new string('m', 12000) + "\n## Latest handoff\n" + new string('t', 3000);
                string metadata = "## Mission Metadata\n## Description\n" + description;
                PromptModuleLedger ledger = new PromptModuleLedger();
                string content = ledger.Track("mission.metadata", metadata);

                string bounded = MissionService.EnforceTotalBriefBudget(content, ledger, 9000, description);

                AssertContains("mission-output:msn_judge", bounded, "the artifact reference survives total-budget shrinking");
                AssertTrue(HasCompleteDigest(bounded), "the full digest survives total-budget shrinking");
                await Task.CompletedTask;
            });

            await RunTest("Every report reference and criterion survives repeated shrinking when required lines exceed the budget", async () =>
            {
                string criteria = "## Acceptance Criteria\n- Required behavior one: " + new string('a', 300)
                    + "\n- Required behavior two: " + new string('b', 300) + "\n## Scope\n";
                string firstReference = ReportReference("msn_judge_one");
                string secondReference = ReportReference("msn_judge_two");
                string full = criteria + new string('x', 6000) + "\n" + firstReference
                    + new string('y', 6000) + "\n" + secondReference + new string('z', 6000);

                string first = MissionService.TruncateMissionDescription(full, 500);
                string second = MissionService.TruncateMissionDescription(first, 400);

                AssertContains("Required behavior one", second, "the first mandatory criterion remains");
                AssertContains("Required behavior two", second, "the second mandatory criterion remains");
                AssertContains("mission-output:msn_judge_one", second, "the first complete report remains reachable");
                AssertTrue(HasCompleteDigest(second), "the full report digest remains");
                AssertContains("mission-output:msn_judge_two", second, "the second complete report remains reachable");
                AssertEqual(2, CountOccurrences(second, "UTF-8 SHA-256 "), "both report digests remain");
                AssertEqual(1, CountOccurrences(second, "### Report essentials (Judge msn_judge_one)"), "repeat shrinking does not duplicate the first reference");
                AssertEqual(1, CountOccurrences(second, "### Report essentials (Judge msn_judge_two)"), "repeat shrinking does not duplicate the second reference");
                AssertTrue(second.Length > 400, "mandatory criteria and report references may exceed a smaller limit");
                await Task.CompletedTask;
            });

            await RunTest("Malformed and mismatched report-reference lookalikes remain trim-able narrative", async () =>
            {
                string[] lookalikes =
                {
                    "### Report essentials (Judge msn_fake)\nComplete output: mission-output:msn_fake (9000 chars, UTF-8 SHA-256 abc123). Read it with armada_mission_output before acting on anything this summary leaves out.\n",
                    "### Report essentials (Judge msn_heading)\nComplete output: mission-output:msn_other (9000 chars, UTF-8 SHA-256 " + new string('a', 64) + "). Read it with armada_mission_output before acting on anything this summary leaves out.\n",
                    "### Report essentials (Judge msn_suffix)\nComplete output: mission-output:msn_suffix (9000 chars, UTF-8 SHA-256 " + new string('b', 64) + "). Different instruction suffix.\n",
                    "Complete Judge review output: mission-output:msn_rscdigest (9000 chars, UTF-8 SHA-256 abc123).\nRead all pages with armada_mission_output; continue until hasMore is false, then verify complete and sha256 before treating the review as complete.\n",
                    "Complete failed mission output: mission-output:msn_rscnotice (9000 chars, UTF-8 SHA-256 " + new string('c', 64) + ").\nRead the complete output with armada_mission_output before editing.\n",
                    "Persisted Judge output (empty): mission-output:msn_rscfallback (0 chars, UTF-8 SHA-256 " + new string('d', 64) + ").\nThe stored output is empty; ReviewComment is only a fallback.\n"
                };
                foreach (string lookalike in lookalikes)
                {
                    string full = "## Mission brief\n" + new string('h', 5000) + "\n" + lookalike
                        + new string('m', 12000) + "\n## Latest handoff\n" + new string('t', 3000);

                    string bounded = MissionService.BoundMetadataDescription(full);

                    AssertFalse(bounded.Contains(lookalike.TrimEnd(), StringComparison.Ordinal), "a lookalike reference is not pinned");
                }
                await Task.CompletedTask;
            });

            await RunTest("Production rescue output references survive all brief trimming paths", async () =>
            {
                foreach (string kind in new[] { "judge", "worker", "incomplete", "fallback" })
                {
                    string missionId = "msn_rsc" + kind;
                    string rescueDescription = BuildRescueDescription(kind, missionId, out Mission failedMission);
                    string referencePair = RescueReferencePair(rescueDescription, missionId);
                    MissionOutputArtifactPage artifact = MissionOutputArtifact.Build(failedMission);
                    string largeCriteria = "## Acceptance Criteria\n" + String.Join("\n", Enumerable.Range(0, 10)
                        .Select(index => "- Required behavior " + index + ": " + new string((char)('a' + index), 1200))) + "\n";
                    string full = largeCriteria + new string('p', 9000) + "\n" + rescueDescription
                        + "\n### Diff from prior stage\n```diff\n" + new string('+', 9000) + "\n```\n"
                        + "## Voyage Board Notes\n" + String.Join("\n", Enumerable.Range(0, 10)
                            .Select(index => "- Note " + index + ": " + new string('n', 500)));

                    string truncated = MissionService.TruncateMissionDescription(full, 20000, "armada/rescue-reference-test");
                    string metadataBounded = MissionService.BoundMetadataDescription(full);
                    PromptModuleLedger ledger = new PromptModuleLedger();
                    string metadata = "## Mission Metadata\n## Description\n" + full;
                    string content = ledger.Track("mission.metadata", metadata);
                    string budgetBounded = MissionService.EnforceTotalBriefBudget(content, ledger, 6000, full);

                    foreach (string bounded in new[] { truncated, metadataBounded, budgetBounded })
                    {
                        string normalizedBounded = bounded.Replace("\r\n", "\n");
                        AssertContains(referencePair, normalizedBounded, kind + " keeps its complete generated reference and safety notice");
                        AssertContains("(" + artifact.TotalLength + " chars, UTF-8 SHA-256 " + artifact.Sha256 + ").", bounded,
                            kind + " keeps the exact artifact length and digest");
                        if (kind == "fallback")
                            AssertContains("Read and verify the source mission record", bounded,
                                "fallback keeps the warning when source output is empty");
                        else
                        {
                            AssertContains("armada_mission_output", bounded, kind + " keeps the output reader instruction");
                            AssertContains("continue until hasMore is false, then verify complete and sha256", bounded,
                                kind + " keeps the complete-output reader instruction");
                        }
                    }

                    string firstShrink = MissionService.TruncateMissionDescription(full, 500, "armada/rescue-reference-test");
                    string secondShrink = MissionService.TruncateMissionDescription(firstShrink, 200, "armada/rescue-reference-test");
                    string normalizedSecondShrink = secondShrink.Replace("\r\n", "\n");
                    AssertContains(referencePair, normalizedSecondShrink, kind + " keeps the reference after repeated shrinking");
                    AssertEqual(1, CountOccurrences(normalizedSecondShrink, referencePair.Split('\n')[0]),
                        kind + " does not duplicate the pinned rescue reference after repeated shrinking");
                }
                await Task.CompletedTask;
            });

            await RunTest("TruncateMissionDescription marker names the branch holding the full change", async () =>
            {
                string oversized = new string('x', 500);
                string bounded = MissionService.TruncateMissionDescription(oversized, 200, "armada/brief-test");

                AssertTrue(bounded.Length <= 200, "bounded description must fit the budget");
                AssertContains("armada/brief-test", bounded, "the cut marker must name the branch so the captain knows where the full change lives");

                string anonymous = MissionService.TruncateMissionDescription(oversized, 200);
                AssertContains("on the branch", anonymous, "an unnamed branch still states that the change is on a branch");

                await Task.CompletedTask;
            });

            await RunTest("TruncateMissionDescription begins the tail at a diff file-section boundary", async () =>
            {
                // A prior-stage diff with two file sections; the budget is sized so the raw tail cut lands
                // inside the first section's content lines. The tail must snap forward to the next
                // `diff --git` header instead of opening mid-file on a hunk line.
                string padding = new string('p', 200) + "\n";
                string diff =
                    "diff --git a/src/One.cs b/src/One.cs\n" +
                    "+one-a\n" +
                    "+one-b\n" +
                    "+one-c\n" +
                    "diff --git a/src/Two.cs b/src/Two.cs\n" +
                    "+two\n";
                string full = padding + diff;

                string bounded = MissionService.TruncateMissionDescription(full, 229, "armada/brief-test");

                AssertContains("diff --git a/src/Two.cs", bounded, "the tail must open at the next file-section header");
                AssertFalse(bounded.Contains("+one-a"), "the first section's content must be elided, not shown half-open");
                AssertFalse(bounded.Contains("+one-b"), "the first section's content must be elided, not shown half-open");
                AssertFalse(bounded.Contains("+one-c"), "the first section's content must be elided, not shown half-open");

                await Task.CompletedTask;
            });

            await RunTest("BoundMetadataDescription leaves a fitting description unchanged", async () =>
            {
                string small = "## Mission brief\nA modest scope.";
                AssertEqual(small, MissionService.BoundMetadataDescription(small), "a description under the metadata cap must be unchanged");
                AssertEqual("No additional description provided.", MissionService.BoundMetadataDescription(""), "an empty description must render the default, not a blank section");
                AssertEqual("No additional description provided.", MissionService.BoundMetadataDescription(null), "a null description must render the default, not a blank section");

                await Task.CompletedTask;
            });

            await RunTest("BoundMetadataDescription caps an oversized description and keeps head and tail", async () =>
            {
                // A long accumulated handoff chain (the rescue-Judge shape) once produced a 53 KB metadata
                // module against a 32 KiB brief budget. The metadata module must be bounded no matter how
                // large the persisted description is, and the newest handoff block (the diff) must survive.
                string head = "## Mission brief\nWire the landed recovery rows through the reset path.\n";
                string middle = new string('m', 30000);
                string tail = "\n\n---\n" + MissionService.BuildHandoffMarker(_OtherUpstreamId) + "\n" +
                    "## Prior Stage Output\n### Diff from prior stage\n```diff\n+recovery rows now reach the runner\n```\n";

                string full = head + middle + tail;
                string bounded = MissionService.BoundMetadataDescription(full);

                AssertTrue(bounded.Length <= MissionService._MaxMetadataDescriptionChars, "metadata description must fit the module cap");
                AssertContains("Wire the landed recovery rows through the reset path", bounded, "the head brief must survive");
                AssertContains("recovery rows now reach the runner", bounded, "the newest handoff diff must survive");
                AssertContains("elided to fit the captain brief", bounded, "the elision must be visible to the captain");

                await Task.CompletedTask;
            });

            await RunTest("An oversized description is delivered whole as bounded files that the elision marker names", async () =>
            {
                string head = "## Mission brief\nWire the landed recovery rows through the reset path.\n";
                System.Text.StringBuilder middle = new System.Text.StringBuilder();
                for (int i = 0; i < 900; i++) middle.Append("Middle context line " + i + " that the bounded copy elides.\n");
                string tail = "\n\n---\n## Prior Stage Output\n```diff\n+recovery rows now reach the runner\n```\n";
                string full = head + middle.ToString() + tail;

                List<ContextBriefFile> files = MissionService.BuildMissionDescriptionFiles(full);
                AssertTrue(files.Count > 1, "a long description is split across files");
                foreach (ContextBriefFile f in files)
                {
                    AssertTrue(f.Bytes <= BriefFilePacker.MaxFileBytes, f.RelativePath + " fits the byte bound");
                    AssertTrue(f.Content.Split('\n').Length - 1 <= BriefFilePacker.MaxFileLines, f.RelativePath + " fits the line bound");
                    AssertTrue(f.RelativePath.StartsWith(MissionService.MissionDescriptionFolder + "/"), "files live under the mission folder");
                }

                string joined = String.Join("\n", files.Select(f => f.Content));
                for (int i = 0; i < 900; i++)
                    AssertContains("Middle context line " + i + " ", joined, "middle line " + i + " survives in the files");
                AssertContains("recovery rows now reach the runner", joined, "the tail survives in the files");

                string bounded = MissionService.BoundMetadataDescription(full, files);
                AssertTrue(bounded.Length <= MissionService._MaxMetadataDescriptionChars + 400, "the embedded copy stays bounded");
                AssertContains("`" + files[0].RelativePath + "`", bounded, "the marker names the first file");
                AssertContains("`" + files[files.Count - 1].RelativePath + "`", bounded, "the marker names the last file");
                AssertFalse(bounded.Contains("Middle context line 450 "), "the embedded copy still elides the middle");

                AssertEqual(0, MissionService.BuildMissionDescriptionFiles("## Mission brief\nA modest scope.").Count, "a fitting description writes no files");
                AssertEqual(0, MissionService.BuildMissionDescriptionFiles(null).Count, "a null description writes no files");

                await Task.CompletedTask;
            });

            await RunTest("BoundMetadataDescription handles a description with no tail split point", async () =>
            {
                string noNewline = new string('x', MissionService._MaxMetadataDescriptionChars + 500);
                string bounded = MissionService.BoundMetadataDescription(noNewline);

                AssertTrue(bounded.Length <= MissionService._MaxMetadataDescriptionChars + 200, "metadata description must stay near the cap");
                AssertContains("elided to fit the captain brief", bounded, "the elision must be visible");

                await Task.CompletedTask;
            });

            await RunTest("EnforceTotalBriefBudget leaves an in-budget brief unchanged", async () =>
            {
                PromptModuleLedger ledger = new PromptModuleLedger();
                string small = "## Brief\nA small mission.";
                ledger.Track("mission.metadata", small);
                AssertEqual(small, MissionService.EnforceTotalBriefBudget(small, ledger, 32768), "an in-budget brief must be unchanged");
                AssertEqual("", MissionService.EnforceTotalBriefBudget("", ledger, 32768), "an empty brief must stay empty");
                AssertEqual(small, MissionService.EnforceTotalBriefBudget(small, ledger, 0), "a disabled budget must leave the brief unchanged");

                await Task.CompletedTask;
            });

            await RunTest("EnforceTotalBriefBudget elides content modules until the brief fits", async () =>
            {
                PromptModuleLedger ledger = new PromptModuleLedger();
                string persona = "## Captain Instructions\nYou are an Armada worker.\n";
                string rules = "## Rules\nStay in scope.\n";
                string objective = "## Objective Scope (Definition of Done)\n" + new string('o', 30000) + "\n";
                string existing = "## Existing Project Instructions\n" + new string('e', 30000) + "\n";

                string brief = persona + rules + objective + existing;
                ledger.Track("mission.captain_instructions_wrapper", persona);
                ledger.Track("mission.rules", rules);
                ledger.Track("mission.objective_scope", objective);
                ledger.Track("mission.existing_instructions_wrapper", existing);

                string bounded = MissionService.EnforceTotalBriefBudget(brief, ledger, 32768);

                AssertTrue(System.Text.Encoding.UTF8.GetByteCount(bounded) <= 32768, "the brief must fit the budget after elision");
                AssertContains("You are an Armada worker", bounded, "the persona must never be elided");
                AssertContains("Stay in scope", bounded, "the rules must never be elided");
                AssertContains("elided to fit the captain brief budget", bounded, "the elision must be visible");
                AssertTrue(bounded.Length < brief.Length, "the brief must shrink");

                await Task.CompletedTask;
            });

            await RunTest("EnforceTotalBriefBudget elides largest content module first, keeps small ones whole", async () =>
            {
                PromptModuleLedger ledger = new PromptModuleLedger();
                string smallObjective = "## Objective Scope (Definition of Done)\nSmall scope.\n";
                string hugeExisting = "## Existing Project Instructions\n" + new string('e', 40000) + "\n";

                string brief = smallObjective + hugeExisting;
                ledger.Track("mission.objective_scope", smallObjective);
                ledger.Track("mission.existing_instructions_wrapper", hugeExisting);

                string bounded = MissionService.EnforceTotalBriefBudget(brief, ledger, 32768);

                AssertTrue(System.Text.Encoding.UTF8.GetByteCount(bounded) <= 32768, "the brief must fit the budget after elision");
                AssertContains("Small scope", bounded, "the small module must survive whole");
                AssertContains("elided to fit the captain brief budget", bounded, "the large module must be elided");

                await Task.CompletedTask;
            });

            await RunTest("IsElidableBriefModule covers content modules and protects the skeleton", async () =>
            {
                AssertTrue(MissionService.IsElidableBriefModule("mission.objective_scope"), "objective scope is content");
                AssertTrue(MissionService.IsElidableBriefModule("mission.existing_instructions_wrapper"), "existing instructions are content");
                AssertTrue(MissionService.IsElidableBriefModule("mission.project_context_wrapper"), "project context is content");
                AssertTrue(MissionService.IsElidableBriefModule("mission.model_context_wrapper"), "model context is content");
                AssertFalse(MissionService.IsElidableBriefModule("mission.persona"), "the persona is never elidable");
                AssertFalse(MissionService.IsElidableBriefModule("mission.rules"), "the rules are never elidable");
                AssertFalse(MissionService.IsElidableBriefModule("mission.metadata"), "the metadata skeleton is never elidable");
                AssertFalse(MissionService.IsElidableBriefModule(null), "a null module name is never elidable");

                await Task.CompletedTask;
            });

            await RunTest("TestEngineer implementation preamble defers test scope to Objective Scope", async () =>
            {
                string preamble = MissionService.BuildPersonaPreamble("TestEngineer", MissionModeEnum.Implementation);

                AssertContains("Objective Scope", preamble, "implementation-mode TestEngineer must follow the mission's explicit scope");
                AssertContains("when tests are in scope", preamble, "test writing remains the ordinary duty when permitted");
                AssertContains("Review the diff below", preamble, "the TestEngineer still reviews the Worker's changes");
            });

            await RunTest("TestEngineer preamble validates the report in Audit and Research modes", async () =>
            {
                string audit = MissionService.BuildPersonaPreamble("TestEngineer", MissionModeEnum.Audit);
                string research = MissionService.BuildPersonaPreamble("TestEngineer", MissionModeEnum.Research);

                AssertContains("Validate the Report", audit, "audit-mode TestEngineer must validate, not write");
                AssertContains("Do not write tests", audit, "an audit TestEngineer must be forbidden from writing tests");
                AssertFalse(audit.Contains("write unit tests"), "an audit TestEngineer must not be ordered to write tests");
                AssertContains("Validate the Report", research, "research-mode TestEngineer must validate, not write");
                AssertFalse(research.Contains("write unit tests"), "a research TestEngineer must not be ordered to write tests");
            });

            await RunTest("Worker preamble reports in Audit mode and implements in Implementation mode", async () =>
            {
                string audit = MissionService.BuildPersonaPreamble("Worker", MissionModeEnum.Audit);
                string impl = MissionService.BuildPersonaPreamble("Worker", MissionModeEnum.Implementation);

                AssertContains("Investigate and Report", audit, "audit-mode Worker must investigate and report");
                AssertFalse(audit.Contains("implementing code changes"), "an audit Worker must not be ordered to implement");
                AssertContains("Worker (Implement)", impl, "implementation-mode Worker keeps the implement role");
            });

            await RunTest("Judge preamble specializes for report-only modes", async () =>
            {
                string audit = MissionService.BuildPersonaPreamble("Judge", MissionModeEnum.Audit);
                string research = MissionService.BuildPersonaPreamble("Judge", MissionModeEnum.Research);
                string impl = MissionService.BuildPersonaPreamble("Judge", MissionModeEnum.Implementation);

                AssertContains("Review the Report", audit, "audit Judge must validate the report");
                AssertContains("Do not edit, commit, or push", audit, "audit Judge must forbid edits");
                AssertContains("do not order or run implementation tests", audit, "audit Judge must not order tests");
                AssertContains("Review the Report", research, "research Judge must validate the report");
                AssertContains("Do not edit, commit, or push", research, "research Judge must forbid edits");
                AssertContains("do not order or run implementation tests", research, "research Judge must not order tests");
                AssertContains("Judge (Review)", impl, "implementation Judge keeps the code-review role");
                AssertContains("test adequacy", impl, "implementation Judge must retain the code test review");
                AssertFalse(audit.Equals(impl, StringComparison.Ordinal), "report-only Judge preamble must differ from implementation");
            });
        }
    }
}
