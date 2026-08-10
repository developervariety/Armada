namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Services;
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
                AssertContains("brief truncated to fit the mission description budget", bounded, "the cut must be visible to the captain");

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
                AssertFalse(MissionService.IsElidableBriefModule("mission.persona"), "the persona is never elidable");
                AssertFalse(MissionService.IsElidableBriefModule("mission.rules"), "the rules are never elidable");
                AssertFalse(MissionService.IsElidableBriefModule("mission.metadata"), "the metadata skeleton is never elidable");
                AssertFalse(MissionService.IsElidableBriefModule(null), "a null module name is never elidable");

                await Task.CompletedTask;
            });
        }
    }
}
