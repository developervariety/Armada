namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for judging a rescue by what it CHANGED rather than by whether it ran. The case
    /// these exist for ran for a day, drew stall nudges, died on a runtime crash, and left one
    /// changed documentation file - and every liveness measure the platform kept called that a
    /// working rescue.
    /// </summary>
    public class RescueEffectivenessTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Rescue Effectiveness";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A rescue that changed only documentation is flagged", () =>
            {
                RescueEffectivenessAssessment assessment = RescueEffectivenessEvaluator.Assess(
                    new List<string> { "docs/armada-ops.md" }, RescueChangeRequirementEnum.BehaviorChange);

                AssertTrue(assessment.IsIneffective, "Describing the defect is not fixing it.");
                AssertEqual(ChangeSubstanceEnum.DocumentationOnly, assessment.Substance);
                AssertContains("documentation", assessment.Reason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A rescue that changed nothing is flagged", () =>
            {
                RescueEffectivenessAssessment assessment = RescueEffectivenessEvaluator.Assess(
                    new List<string>(), RescueChangeRequirementEnum.BehaviorChange);

                AssertTrue(assessment.IsIneffective);
                AssertEqual(ChangeSubstanceEnum.None, assessment.Substance);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("One behavior-carrying file makes the whole change set substantive", () =>
            {
                RescueEffectivenessAssessment assessment = RescueEffectivenessEvaluator.Assess(
                    new List<string> { "CHANGELOG.md", "docs/notes.md", "src/Thing.cs" }, RescueChangeRequirementEnum.BehaviorChange);

                AssertFalse(assessment.IsIneffective, "A real code change alongside docs is a real change.");
                AssertEqual(ChangeSubstanceEnum.Substantive, assessment.Substance);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A report-only rescue is NEVER flagged for producing no diff", () =>
            {
                // Audit and Research missions deliver a report. Judging them by a diff is the same
                // mistake in the other direction, and it once marked correct work Failed.
                RescueEffectivenessAssessment none = RescueEffectivenessEvaluator.Assess(new List<string>(), RescueChangeRequirementEnum.None);
                RescueEffectivenessAssessment docs = RescueEffectivenessEvaluator.Assess(
                    new List<string> { "docs/findings.md" }, RescueChangeRequirementEnum.None);

                AssertFalse(none.IsIneffective, "A read-only mission that produces no commit has succeeded.");
                AssertFalse(docs.IsIneffective);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A rescue's owed change follows its mode and the objective kind's declared deliverable", () =>
            {
                // Research delivers findings and owes nothing; Chore delivers a committed document and
                // owes a commit; every other kind owes behavior. Only Implementation mode owes
                // anything. Cover every mode x kind pair, not the cases that prompted the rule.
                foreach (MissionModeEnum mode in System.Enum.GetValues<MissionModeEnum>())
                {
                    bool implementation = mode == MissionModeEnum.Implementation;
                    AssertEqual(
                        implementation ? RescueChangeRequirementEnum.BehaviorChange : RescueChangeRequirementEnum.None,
                        RescueEffectivenessEvaluator.RequiredChange(mode, null),
                        "no linked objective, mode " + mode);
                    foreach (ObjectiveKindEnum kind in System.Enum.GetValues<ObjectiveKindEnum>())
                    {
                        RescueChangeRequirementEnum expected = !implementation || kind == ObjectiveKindEnum.Research
                            ? RescueChangeRequirementEnum.None
                            : kind == ObjectiveKindEnum.Chore
                                ? RescueChangeRequirementEnum.AnyCommittedChange
                                : RescueChangeRequirementEnum.BehaviorChange;
                        AssertEqual(expected, RescueEffectivenessEvaluator.RequiredChange(mode, kind),
                            "mode " + mode + ", objective kind " + kind);
                    }
                }

                RescueEffectivenessAssessment census = RescueEffectivenessEvaluator.Assess(
                    new List<string> { "discoveries.d/data-census.md" },
                    RescueEffectivenessEvaluator.RequiredChange(MissionModeEnum.Implementation, ObjectiveKindEnum.Research));
                AssertFalse(census.IsIneffective, "A census delivered as a document under a Research objective is the work.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A committed-document rescue under a Chore objective is effective; under a code kind it is not", () =>
            {
                // A Chore objective's deliverable is a committed document (an audit report, a
                // discoveries record, a census). Its rescue fixing that document is the work.
                List<string> reportOnly = new List<string> { "docs/audits/port-coverage-audit.md" };
                RescueEffectivenessAssessment chore = RescueEffectivenessEvaluator.Assess(
                    reportOnly,
                    RescueEffectivenessEvaluator.RequiredChange(MissionModeEnum.Implementation, ObjectiveKindEnum.Chore));
                AssertFalse(chore.IsIneffective, "A Chore rescue that commits its report delivered the report.");

                foreach (ObjectiveKindEnum codeKind in new[] { ObjectiveKindEnum.Feature, ObjectiveKindEnum.Bug, ObjectiveKindEnum.Refactor })
                {
                    RescueEffectivenessAssessment code = RescueEffectivenessEvaluator.Assess(
                        reportOnly,
                        RescueEffectivenessEvaluator.RequiredChange(MissionModeEnum.Implementation, codeKind));
                    AssertTrue(code.IsIneffective, "A " + codeKind + " rescue that only wrote documentation described the defect.");
                }

                RescueEffectivenessAssessment emptyChore = RescueEffectivenessEvaluator.Assess(
                    new List<string>(),
                    RescueEffectivenessEvaluator.RequiredChange(MissionModeEnum.Implementation, ObjectiveKindEnum.Chore));
                AssertTrue(emptyChore.IsIneffective, "A Chore rescue that committed nothing did not deliver its document.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Documentation is recognized by extension, directory, and bare name", () =>
            {
                AssertTrue(ChangeSubstanceClassifier.IsDocumentation("README.md"));
                AssertTrue(ChangeSubstanceClassifier.IsDocumentation("notes.txt"));
                AssertTrue(ChangeSubstanceClassifier.IsDocumentation("guide.rst"));
                AssertTrue(ChangeSubstanceClassifier.IsDocumentation("docs/deep/nested/diagram.png"),
                    "Anything under a docs directory is documentation, whatever its type.");
                AssertTrue(ChangeSubstanceClassifier.IsDocumentation("LICENSE"));
                AssertTrue(ChangeSubstanceClassifier.IsDocumentation("CHANGELOG"));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Source and config files are NOT documentation", () =>
            {
                AssertFalse(ChangeSubstanceClassifier.IsDocumentation("src/Armada.Core/Thing.cs"));
                AssertFalse(ChangeSubstanceClassifier.IsDocumentation("build.props"));
                AssertFalse(ChangeSubstanceClassifier.IsDocumentation("settings.json"));
                AssertFalse(ChangeSubstanceClassifier.IsDocumentation("Program.cs"));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A path whose directory merely CONTAINS 'doc' is not documentation", () =>
            {
                // "Docker" and "DocumentStore" must not be swallowed by a substring match.
                AssertFalse(ChangeSubstanceClassifier.IsDocumentation("docker/Dockerfile"));
                AssertFalse(ChangeSubstanceClassifier.IsDocumentation("src/DocumentStore/Writer.cs"));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Changed paths are read from the diff headers only", () =>
            {
                // A hunk body can contain a line that looks like a header. Trusting it would let a
                // change set describe itself as touching files it never touched.
                string diff = string.Join("\n", new[]
                {
                    "diff --git a/src/Real.cs b/src/Real.cs",
                    "index 111..222 100644",
                    "--- a/src/Real.cs",
                    "+++ b/src/Real.cs",
                    "@@ -1 +1 @@",
                    "-old",
                    "+diff --git a/src/Fake.cs b/src/Fake.cs"
                });

                IReadOnlyList<string> paths = DiffPathExtractor.ExtractChangedPaths(diff);

                AssertEqual(1, paths.Count, "Only the real header counts.");
                AssertEqual("src/Real.cs", paths[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Several files and a rename resolve to their post-change names", () =>
            {
                string diff = string.Join("\n", new[]
                {
                    "diff --git a/docs/a.md b/docs/a.md",
                    "@@ -1 +1 @@",
                    "diff --git a/src/Old.cs b/src/New.cs",
                    "@@ -1 +1 @@"
                });

                IReadOnlyList<string> paths = DiffPathExtractor.ExtractChangedPaths(diff);

                AssertEqual(2, paths.Count);
                AssertEqual("docs/a.md", paths[0]);
                AssertEqual("src/New.cs", paths[1], "A rename reports where the file ended up.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An empty or null diff yields no paths", () =>
            {
                AssertEqual(0, DiffPathExtractor.ExtractChangedPaths(null).Count);
                AssertEqual(0, DiffPathExtractor.ExtractChangedPaths("").Count);
                AssertEqual(0, DiffPathExtractor.ExtractChangedPaths("   \n  ").Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The end-to-end shape: a docs-only rescue diff is flagged", () =>
            {
                string diff = "diff --git a/docs/armada-ops.md b/docs/armada-ops.md\n@@ -1 +1 @@\n-a\n+b";

                RescueEffectivenessAssessment assessment = RescueEffectivenessEvaluator.Assess(
                    DiffPathExtractor.ExtractChangedPaths(diff), RescueChangeRequirementEnum.BehaviorChange);

                AssertTrue(assessment.IsIneffective, "This is the twenty-four-hour rescue, reduced to its evidence.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A rescue is identified from ONE definition, by marker or legacy title", () =>
            {
                AssertTrue(RescueMissionMarker.IsAutoRescue(
                    new Mission { Description = RescueMissionMarker.Marker + "\nfix it" }));
                AssertTrue(RescueMissionMarker.IsAutoRescue(new Mission { Title = "Rescue: something" }));
                AssertFalse(RescueMissionMarker.IsAutoRescue(new Mission { Title = "Port the decoder" }));
                AssertFalse(RescueMissionMarker.IsAutoRescue(null));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
