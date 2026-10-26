namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Regression coverage for durable, digest-backed mission output pages.
    /// </summary>
    public sealed class MissionOutputArtifactTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Output Artifact";

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Pages reconstruct complete Unicode output and preserve digest", () =>
            {
                string output = "first🙂secondéthird";
                Mission mission = BuildMission(output, MissionStatusEnum.Complete);
                StringBuilder rebuilt = new StringBuilder();
                int offset = 0;
                MissionOutputArtifactPage page;
                do
                {
                    page = MissionOutputArtifact.Build(mission, offset, 6);
                    rebuilt.Append(page.Content);
                    offset = page.NextOffset ?? page.TotalLength;
                }
                while (page.HasMore);

                string expectedDigest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
                AssertEqual(output, rebuilt.ToString(), "Paging must not split or replace Unicode data.");
                AssertEqual(expectedDigest, page.Sha256, "Every page must expose the digest for the full output.");
                AssertEqual(Encoding.UTF8.GetByteCount(output), page.TotalUtf8Bytes);
                AssertTrue(page.Finalized);
                AssertTrue(page.Complete);
                AssertNull(page.TruncationReason);
                return Task.CompletedTask;
            });

            await RunTest("Offset inside a surrogate pair is normalized without corrupting output", () =>
            {
                Mission mission = BuildMission("A🙂B", MissionStatusEnum.Complete);
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission, 2, 1);

                AssertEqual(1, page.Offset, "A low-surrogate offset must move to the rune boundary.");
                AssertEqual("🙂", page.Content);
                AssertEqual(3, page.NextOffset);
                return Task.CompletedTask;
            });

            await RunTest("Capture truncation is an explicit finalized evidence gap", () =>
            {
                Mission mission = BuildMission(
                    MissionOutputArtifact.StreamTruncationMarker + "\nretained tail",
                    MissionStatusEnum.Complete);
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission);

                AssertTrue(page.Finalized);
                AssertFalse(page.Complete);
                AssertEqual("stream_capture_limit", page.TruncationReason);
                return Task.CompletedTask;
            });

            await RunTest("Missing and live output state are explicit", () =>
            {
                Mission missing = BuildMission(null, MissionStatusEnum.Complete);
                MissionOutputArtifactPage missingPage = MissionOutputArtifact.Build(missing);
                AssertTrue(missingPage.Finalized);
                AssertFalse(missingPage.Complete);
                AssertEqual("output_unavailable", missingPage.TruncationReason);

                Mission live = BuildMission("partial", MissionStatusEnum.InProgress);
                MissionOutputArtifactPage livePage = MissionOutputArtifact.Build(live);
                AssertFalse(livePage.Finalized);
                AssertFalse(livePage.Complete);
                AssertEqual("mission_not_finalized", livePage.TruncationReason);
                return Task.CompletedTask;
            });

            await RunTest("Page requests are bounded and stable", () =>
            {
                Mission mission = BuildMission(new string('x', MissionOutputArtifact.MaximumPageLength + 5), MissionStatusEnum.Complete);
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission, 0, Int32.MaxValue);
                AssertEqual(0, page.Offset);
                AssertEqual(MissionOutputArtifact.MaximumPageLength, page.Length);
                AssertTrue(page.HasMore);
                AssertEqual(MissionOutputArtifact.MaximumPageLength, page.NextOffset);
                return Task.CompletedTask;
            });

            await RunTest("Artifact pages and digest use the same deterministic redacted text", () =>
            {
                Mission mission = BuildMission("token=testsecret\nresult ok", MissionStatusEnum.Complete);
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission);
                AssertFalse(page.Content.Contains("testsecret", StringComparison.Ordinal));
                AssertContains("token=[REDACTED]", page.Content);
                string digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(page.Content))).ToLowerInvariant();
                AssertEqual(digest, page.Sha256, "The digest must cover the exact safe artifact returned to callers.");
                return Task.CompletedTask;
            });

            await RunTest("Every page carries the joined digest and length and its own page digest", () =>
            {
                string output = "alpha🙂beta\ngamma é delta\n" + new string('z', 40);
                Mission mission = BuildMission(output, MissionStatusEnum.Complete);
                string joinedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
                StringBuilder rebuilt = new StringBuilder();
                int offset = 0;
                int pages = 0;
                MissionOutputArtifactPage page;
                do
                {
                    page = MissionOutputArtifact.Build(mission, offset, 9);
                    pages++;
                    AssertEqual(joinedDigest, page.Sha256, "every page names the digest of the joined content");
                    AssertEqual(output.Length, page.TotalLength, "every page names the joined length");
                    AssertEqual(Encoding.UTF8.GetByteCount(output), page.TotalUtf8Bytes, "every page names the joined UTF-8 byte count");
                    AssertEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(page.Content))).ToLowerInvariant(), page.PageSha256, "the page digest covers this page's content alone");
                    AssertEqual(Encoding.UTF8.GetByteCount(page.Content), page.PageUtf8Bytes, "the page byte count covers this page's content alone");
                    rebuilt.Append(page.Content);
                    offset = page.NextOffset ?? page.TotalLength;
                }
                while (page.HasMore);
                AssertTrue(pages > 1, "the output spans several pages");
                AssertEqual(output, rebuilt.ToString());
                return Task.CompletedTask;
            });

            await RunTest("A finished Test Engineer report without its contract sections is not a complete artifact", () =>
            {
                Mission progressOnly = BuildMission("Running the focused suite now; will report the matrix once it finishes.", MissionStatusEnum.Complete);
                progressOnly.Persona = "Test Engineer";
                MissionOutputArtifactPage page = MissionOutputArtifact.Build(progressOnly);
                AssertTrue(page.Finalized, "the run has stopped");
                AssertFalse(page.Complete, "a progress note is not the stage report");
                AssertEqual("report_contract_missing", page.TruncationReason);
                AssertEqual(3, page.MissingReportSections.Count, "every missing section is named");
                AssertTrue(page.MissingReportSections.Contains("Coverage Added"));
                AssertTrue(page.MissingReportSections.Contains("Negative Paths"));
                AssertTrue(page.MissingReportSections.Contains("Residual Risks"));

                Mission legacyName = BuildMission("## Coverage Added\n- one test\n## Residual Risks\nnone\n[ARMADA:RESULT] COMPLETE", MissionStatusEnum.WorkProduced);
                legacyName.Persona = "TestEngineer";
                MissionOutputArtifactPage partial = MissionOutputArtifact.Build(legacyName);
                AssertFalse(partial.Complete, "the legacy persona name carries the same contract");
                AssertEqual(1, partial.MissingReportSections.Count);
                AssertEqual("Negative Paths", partial.MissingReportSections[0]);

                const string reportOnly = "## Findings\nThe preserved metadata and command digest were independently verified.\n" +
                    "## Validation\nRequired-removal and wrong-type controls reject the invalid records.\n" +
                    "## Residual Risk\nHistorical inputs remain unavailable; no behavioral acceptance is claimed.\n" +
                    "[ARMADA:RESULT] COMPLETE";
                foreach (MissionModeEnum mode in new[] { MissionModeEnum.Implementation, MissionModeEnum.Audit, MissionModeEnum.Research })
                {
                    Mission report = BuildMission(reportOnly, MissionStatusEnum.WorkProduced);
                    report.Persona = "TestEngineer";
                    report.Mode = mode;
                    MissionOutputArtifactPage artifact = MissionOutputArtifact.Build(report);
                    bool reportMode = mode != MissionModeEnum.Implementation;
                    AssertEqual(reportMode, artifact.Complete, "capture completeness follows the generated contract for " + mode);
                    AssertEqual(reportMode ? 0 : 3, artifact.MissingReportSections.Count,
                        "only Implementation requires test-creation sections");
                    AssertEqual(reportOnly, artifact.Content, "mode changes no captured bytes");
                    AssertTrue(artifact.Finalized && !artifact.HasMore, "the full finished report was captured");
                    if (reportMode)
                    {
                        AssertNull(artifact.TruncationReason);
                        string contract = MissionPromptBuilder.GetPersonaOutputContract(report.Persona, mode);
                        AssertContains("deliverable is a report", contract, "the actual prompt asks for a report");
                        AssertFalse(contract.Contains("Coverage Added", StringComparison.Ordinal),
                            "the prompt does not require Implementation coverage headings");
                    }
                }
                return Task.CompletedTask;
            });

            await RunTest("A progress-status line joined to a real section preserves artifact bytes and section recognition", () =>
            {
                const string tail = "\nA bounded result.\n## Negative Paths\nA rejected input.\n## Residual Risks\nNo semantic approval is inferred.\n";
                foreach (string status in new[] { "InProgress", "Testing", "Review" })
                {
                    string text = "Earlier progress.\n[ARMADA:STATUS] " + status + "## Coverage Added" + tail;
                    Mission mission = BuildMission(text, MissionStatusEnum.Complete);
                    mission.Persona = "TestEngineer";
                    MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission);
                    AssertTrue(page.Complete, "An explicit heading after a recognized progress status is present: " + status);
                    AssertEqual(text, page.Content, "Read-time heading recognition does not rewrite the report");
                    AssertEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(), page.Sha256);
                    AssertTrue(page.Finalized && !page.HasMore);
                    AssertEqual(0, page.MissingReportSections.Count);
                }
                string[] invalid =
                {
                    "Prose [ARMADA:STATUS] Testing## Coverage Added",
                    "> [ARMADA:STATUS] Testing## Coverage Added",
                    "    [ARMADA:STATUS] Testing## Coverage Added",
                    "\t[ARMADA:STATUS] Testing## Coverage Added",
                    " \t[ARMADA:STATUS] Testing## Coverage Added",
                    "[ARMADA:STATUS] Unknown## Coverage Added",
                    "[ARMADA:STATUS] Complete## Coverage Added",
                    "[ARMADA:STATUS] Testing#Coverage Added",
                    "```text\n[ARMADA:STATUS] Testing## Coverage Added\n```",
                    "~~~\n[ARMADA:STATUS] Testing## Coverage Added\n~~~",
                    "````\n```\n[ARMADA:STATUS] Testing## Coverage Added\n````"
                };
                foreach (string prefix in invalid)
                {
                    Mission mission = BuildMission(prefix + tail, MissionStatusEnum.Complete);
                    mission.Persona = "TestEngineer";
                    MissionOutputArtifactPage page = MissionOutputArtifact.Build(mission);
                    AssertFalse(page.Complete, "An example or malformed status does not supply a section: " + prefix);
                    AssertTrue(page.MissingReportSections.Contains("Coverage Added"));
                    AssertEqual(prefix + tail, page.Content);
                }
                Mission afterIndentedFence = BuildMission("\t```text\n\n[ARMADA:STATUS] Testing## Coverage Added" + tail, MissionStatusEnum.Complete);
                afterIndentedFence.Persona = "TestEngineer";
                AssertTrue(MissionOutputArtifact.Build(afterIndentedFence).Complete,
                    "A code-indented fence example does not hide the later unindented heading");
                return Task.CompletedTask;
            });

            await RunTest("A report with every contract section, a failed stage, and a persona without a contract stay as captured", () =>
            {
                Mission full = BuildMission("## Coverage Added\n- one\n\n### negative paths\n- one\n\n## Residual Risks\nnone\n[ARMADA:RESULT] COMPLETE", MissionStatusEnum.Complete);
                full.Persona = "Test Engineer";
                MissionOutputArtifactPage fullPage = MissionOutputArtifact.Build(full);
                AssertTrue(fullPage.Complete, "headings match at any level and in any case");
                AssertNull(fullPage.TruncationReason);
                AssertEqual(0, fullPage.MissingReportSections.Count);

                Mission failed = BuildMission("crashed while running tests", MissionStatusEnum.Failed);
                failed.Persona = "Test Engineer";
                MissionOutputArtifactPage failedPage = MissionOutputArtifact.Build(failed);
                AssertTrue(failedPage.Complete, "a failed stage is not held to the success report contract");
                AssertEqual(0, failedPage.MissingReportSections.Count);

                Mission worker = BuildMission("done", MissionStatusEnum.Complete);
                worker.Persona = "Worker";
                AssertTrue(MissionOutputArtifact.Build(worker).Complete, "a persona without a fixed contract is unaffected");

                Mission productManager = BuildMission("## Product Vision\nx\n## Use Cases\nx\n## Validation\nx", MissionStatusEnum.Complete);
                productManager.Persona = "Product Manager";
                MissionOutputArtifactPage pmPage = MissionOutputArtifact.Build(productManager);
                AssertFalse(pmPage.Complete);
                AssertEqual(2, pmPage.MissingReportSections.Count, "the Product Manager contract names its two missing sections");
                foreach (MissionModeEnum mode in new[] { MissionModeEnum.Audit, MissionModeEnum.Research })
                {
                    productManager.Mode = mode;
                    MissionOutputArtifactPage reportPage = MissionOutputArtifact.Build(productManager);
                    AssertTrue(reportPage.Complete, "the report-only product prompt leaves section shape to its mission");
                    AssertEqual(0, reportPage.MissingReportSections.Count);
                    AssertContains("mission description controls the report's shape",
                        MissionPromptBuilder.GetPersonaOutputContract(productManager.Persona, mode));
                }
                return Task.CompletedTask;
            });

            await RunTest("A Judge report reads the same missing sections its verdict check refuses, whatever its final status", () =>
            {
                const string brief = "## Acceptance Criteria\n- The report names the consumer.\n";
                const string withoutWalk = "## Completeness\n1. The report names the consumer.\n## Correctness\nSound.\n## Evidence\nStrong.\n"
                    + "## Residual Risks\nNone.\n## Verdict\nPASS\n[ARMADA:VERDICT] PASS";
                Mission refused = BuildMission(withoutWalk, MissionStatusEnum.Failed);
                refused.Persona = "Judge";
                refused.Mode = MissionModeEnum.Research;
                refused.Description = brief;
                MissionOutputArtifactPage refusedPage = MissionOutputArtifact.Build(refused);
                AssertFalse(refusedPage.Complete, "a Judge report without the walk its brief asks for is not complete");
                AssertEqual("report_contract_missing", refusedPage.TruncationReason);
                AssertEqual(1, refusedPage.MissingReportSections.Count, "only the walk is missing: " + string.Join(", ", refusedPage.MissingReportSections));
                AssertEqual(JudgeAcceptanceWalk.SectionName, refusedPage.MissingReportSections[0]);
                AssertEqual("Judge PASS verdict missing required review sections: " + JudgeAcceptanceWalk.SectionName,
                    JudgeAcceptanceWalk.ValidatePass(withoutWalk, brief), "the verdict check refuses the same report for the same section");

                Mission walked = BuildMission(withoutWalk.Replace("## Verdict", "## Acceptance Criteria\n- The report names the consumer. -- MET. report.md:4 names it.\n## Verdict"), MissionStatusEnum.Complete);
                walked.Persona = "Judge";
                walked.Mode = MissionModeEnum.Research;
                walked.Description = brief;
                MissionOutputArtifactPage walkedPage = MissionOutputArtifact.Build(walked);
                AssertTrue(walkedPage.Complete, "a Judge report with every section is complete");
                AssertEqual(0, walkedPage.MissingReportSections.Count);

                Mission revision = BuildMission("Two findings need a fix.\n[ARMADA:VERDICT] NEEDS_REVISION", MissionStatusEnum.Failed);
                revision.Persona = "Judge";
                revision.Mode = MissionModeEnum.Research;
                revision.Description = brief;
                AssertTrue(MissionOutputArtifact.Build(revision).Complete, "a NEEDS_REVISION review is not checked for sections, as in the verdict check");
                return Task.CompletedTask;
            });

            await RunTest("Invalid page requests fail explicitly", () =>
            {
                Mission mission = BuildMission("short", MissionStatusEnum.Complete);
                bool negativeFailed = false;
                bool beyondFailed = false;
                try { MissionOutputArtifact.Build(mission, -1, 1); }
                catch (ArgumentOutOfRangeException) { negativeFailed = true; }
                try { MissionOutputArtifact.Build(mission, 99, 1); }
                catch (ArgumentOutOfRangeException) { beyondFailed = true; }
                AssertTrue(negativeFailed, "Negative offsets must not silently normalize.");
                AssertTrue(beyondFailed, "Offsets beyond the artifact must not look like a successful final page.");
                return Task.CompletedTask;
            });
        }

        private static Mission BuildMission(string? output, MissionStatusEnum status)
        {
            return new Mission("output artifact test")
            {
                Id = "mission-output-test",
                AgentOutput = output,
                Status = status
            };
        }
    }
}
