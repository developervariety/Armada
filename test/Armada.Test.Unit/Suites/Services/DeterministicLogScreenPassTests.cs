namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the deterministic captain-log screening pass. Every rule class is proved with a
    /// tail that violates it and with a tail that looks like the violation and is not, because a
    /// false positive on this pass is noise on the operator board.
    /// </summary>
    public sealed class DeterministicLogScreenPassTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Deterministic Log Screen Pass";

        // The plan-label fixtures are composed at run time rather than written as literals: the shape
        // this rule detects must not itself appear in committed source.
        private static readonly string _PlanLabel = "M" + "2" + ":";

        private static LogScreenContext Context(params string[] lines)
        {
            return new LogScreenContext
            {
                MissionId = "mission-under-test",
                Tail = String.Join("\n", lines)
            };
        }

        private static async Task<List<string>> ClassesAsync(CaptainLogScreeningSettings settings, LogScreenContext context)
        {
            DeterministicLogScreenPass pass = new DeterministicLogScreenPass(settings);
            IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
            return findings.Select(f => f.RuleClass).ToList();
        }

        private static Task<List<string>> ClassesAsync(LogScreenContext context)
        {
            return ClassesAsync(new CaptainLogScreeningSettings(), context);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("UnprovedFix_ClaimBackedOnlyByToolSuccessMessage_IsReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "Applied the change to the decoder.",
                    "The fix is verified: Build succeeded with 0 errors.")).ConfigureAwait(false);
                AssertTrue(classes.Contains(DeterministicLogScreenPass.RuleUnprovedFix));
            });

            await RunTest("UnprovedFix_ClaimWithReRunOfFailingCommand_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "Re-ran the command that failed and captured its output.",
                    "The fix is verified: Build succeeded with 0 errors.")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RuleUnprovedFix));
            });

            await RunTest("UnprovedFix_ToolSuccessMessageAlone_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "Build succeeded with 0 errors.",
                    "Continuing to the next step.")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RuleUnprovedFix));
            });

            await RunTest("PipeGatedBuild_BuildPipedToGrepChainingTheSuite_IsReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "$ dotnet build 2>&1 | grep -E 'error|Build succeeded' && ./run-tests.sh unit")).ConfigureAwait(false);
                AssertTrue(classes.Contains(DeterministicLogScreenPass.RulePipeGatedBuild));
            });

            await RunTest("PipeGatedBuild_BuildToFileThenGrepTheFile_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "$ dotnet build > b.log 2>&1; grep -q 'Build succeeded' b.log && ./run-tests.sh unit")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RulePipeGatedBuild));
            });

            await RunTest("PipeGatedBuild_BuildPipedToGrepWithNoChainedSuite_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "$ dotnet build 2>&1 | grep -E 'error'")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RulePipeGatedBuild));
            });

            await RunTest("SilentSkip_AddedEmptyCatch_IsReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "diff --git a/Sample.cs b/Sample.cs",
                    "@@ -10,3 +10,6 @@",
                    "     Apply();",
                    "+            catch (Exception)",
                    "+            {",
                    "+            }")).ConfigureAwait(false);
                AssertTrue(classes.Contains(DeterministicLogScreenPass.RuleSilentSkip));
            });

            await RunTest("SilentSkip_AddedCatchThatLogsAndCounts_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "diff --git a/Sample.cs b/Sample.cs",
                    "@@ -10,3 +10,6 @@",
                    "     Apply();",
                    "+            catch (Exception ex)",
                    "+            {",
                    "+                _Logging.Warn(_Header + \"apply failed: \" + ex.Message);",
                    "+                _Failures++;",
                    "+            }")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RuleSilentSkip));
            });

            await RunTest("SilentSkip_AddedSkipWithNoStatedReason_IsReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "diff --git a/SampleTests.cs b/SampleTests.cs",
                    "@@ -1,2 +1,3 @@",
                    " public class SampleTests",
                    "+        [Fact(Skip = \"\")]")).ConfigureAwait(false);
                AssertTrue(classes.Contains(DeterministicLogScreenPass.RuleSilentSkip));
            });

            await RunTest("SilentSkip_AddedSkipWithAStatedReason_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "diff --git a/SampleTests.cs b/SampleTests.cs",
                    "@@ -1,2 +1,3 @@",
                    " public class SampleTests",
                    "+        [Fact(Skip = \"needs a live container runtime\")]")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RuleSilentSkip));
            });

            await RunTest("SilentSkip_EmptyCatchOutsideAnAddedHunk_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "Read the existing handler, which holds catch (Exception) { } already.",
                    "No change was needed there.")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RuleSilentSkip));
            });

            await RunTest("PlanLabel_LabelInAddedContent_IsReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "diff --git a/Sample.cs b/Sample.cs",
                    "@@ -1,1 +1,2 @@",
                    " public void Apply()",
                    "+    // " + _PlanLabel + " retry once before failing")).ConfigureAwait(false);
                AssertTrue(classes.Contains(DeterministicLogScreenPass.RulePlanLabel));
            });

            await RunTest("PlanLabel_BareLabelUsedAsAPointerInProse_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "The plan front-matter names dependsOnMissionId: " + _PlanLabel.TrimEnd(':') + ", so this block runs second.")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RulePlanLabel));
            });

            await RunTest("PlanLabel_LabelShapedIdentifierWithoutAColonInAddedContent_IsNotReported", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "diff --git a/Sample.cs b/Sample.cs",
                    "@@ -1,1 +1,2 @@",
                    " public void Apply()",
                    "+    int m2 = " + _PlanLabel.TrimEnd(':') + " + 1;")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RulePlanLabel));
            });

            await RunTest("BoundaryToken_EmptyPatternListLeavesTheRuleInert", async () =>
            {
                CaptainLogScreeningSettings settings = new CaptainLogScreeningSettings();
                AssertEqual(0, settings.BoundaryPatterns.Count);
                List<string> classes = await ClassesAsync(settings, Context(
                    "Working in the reserved-term area of the tree.")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RuleBoundaryToken));
            });

            await RunTest("BoundaryToken_ConfiguredPatternOnAPlainLine_IsReported", async () =>
            {
                CaptainLogScreeningSettings settings = new CaptainLogScreeningSettings();
                settings.BoundaryPatterns = new List<string> { "reserved-term" };
                List<string> classes = await ClassesAsync(settings, Context(
                    "Working in the reserved-term area of the tree.")).ConfigureAwait(false);
                AssertTrue(classes.Contains(DeterministicLogScreenPass.RuleBoundaryToken));
            });

            await RunTest("BoundaryToken_ConfiguredPatternQuotedInsideACodeFence_IsNotReported", async () =>
            {
                CaptainLogScreeningSettings settings = new CaptainLogScreeningSettings();
                settings.BoundaryPatterns = new List<string> { "reserved-term" };
                List<string> classes = await ClassesAsync(settings, Context(
                    "The pattern list holds:",
                    "```",
                    "reserved-term",
                    "```",
                    "Nothing in the work names it.")).ConfigureAwait(false);
                AssertFalse(classes.Contains(DeterministicLogScreenPass.RuleBoundaryToken));
            });

            await RunTest("Evaluate_CleanTail_ReportsNothing", async () =>
            {
                List<string> classes = await ClassesAsync(Context(
                    "Read the handler and the two call sites.",
                    "Build succeeded.",
                    "Re-ran the failing command; it now returns 0.")).ConfigureAwait(false);
                AssertEqual(0, classes.Count);
            });

            await RunTest("Evaluate_EveryFindingIsARuleWithOneEvidenceLineAndNoConfidence", async () =>
            {
                DeterministicLogScreenPass pass = new DeterministicLogScreenPass(new CaptainLogScreeningSettings());
                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(Context(
                    "$ dotnet build 2>&1 | grep -E 'error' && ./run-tests.sh unit"), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, findings.Count);
                AssertEqual(LogScreenFinding.SourceRule, findings[0].Source);
                AssertTrue(findings[0].Confidence == null);
                AssertFalse(findings[0].EvidenceLine.Contains('\n'));
            });
        }
    }
}
