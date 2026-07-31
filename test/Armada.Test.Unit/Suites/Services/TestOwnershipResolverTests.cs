namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Covers who owns tests for a mission. A prompt must never defer work to a pipeline stage the
    /// dispatch did not create: a single-stage pipeline previously left nobody owning tests while the
    /// Judge still required them.
    /// </summary>
    public class TestOwnershipResolverTests : TestSuite
    {
        public override string Name => "Test Ownership Resolver";

        private Mission MakeMission(string persona, int? stageOrder = null, string? id = null)
        {
            Mission mission = new Mission();
            mission.Persona = persona;
            mission.StageOrder = stageOrder;
            if (!String.IsNullOrEmpty(id)) mission.Id = id!;
            return mission;
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("A lone Worker owns tests", async () =>
            {
                Mission worker = MakeMission("Worker");
                List<Mission> voyage = new List<Mission> { worker };

                AssertEqual(TestOwnershipEnum.SoleTestOwner, TestOwnershipResolver.Resolve(worker, voyage),
                    "no Test Engineer stage means the Worker owns tests");

                string directive = TestOwnershipResolver.BuildDirective("Worker", TestOwnershipEnum.SoleTestOwner);
                AssertContains("You own the tests for this change", directive, "the Worker must be told it owns tests");

                await Task.CompletedTask;
            });

            await RunTest("A Worker followed by a Test Engineer is told a stage follows", async () =>
            {
                Mission worker = MakeMission("Worker", 1);
                Mission tester = MakeMission("TestEngineer", 2);
                List<Mission> voyage = new List<Mission> { worker, tester };

                AssertEqual(TestOwnershipEnum.TestEngineerFollows, TestOwnershipResolver.Resolve(worker, voyage),
                    "a later Test Engineer stage must be detected");

                string directive = TestOwnershipResolver.BuildDirective("Worker", TestOwnershipEnum.TestEngineerFollows);
                AssertContains("A Test Engineer stage runs after you", directive, "the Worker must know a stage follows");
                AssertContains("Do not skip testing", directive, "the Worker must still test its own change");

                await Task.CompletedTask;
            });

            await RunTest("The legacy unspaced persona name is recognized", async () =>
            {
                // Seeded pipelines carry "TestEngineer" while the canonical constant is "Test Engineer".
                Mission tester = MakeMission("TestEngineer");
                AssertEqual(TestOwnershipEnum.TestEngineerIsMe, TestOwnershipResolver.Resolve(tester, new List<Mission> { tester }),
                    "the legacy unspaced name must still resolve");

                Mission spaced = MakeMission("Test Engineer");
                AssertEqual(TestOwnershipEnum.TestEngineerIsMe, TestOwnershipResolver.Resolve(spaced, new List<Mission> { spaced }),
                    "the canonical name must resolve");

                await Task.CompletedTask;
            });

            await RunTest("An unknown pipeline is treated as sole ownership, never as a stage that may not exist", async () =>
            {
                Mission worker = MakeMission("Worker");
                AssertEqual(TestOwnershipEnum.Unknown, TestOwnershipResolver.Resolve(worker, (IReadOnlyList<Mission>?)null),
                    "an unresolvable voyage is Unknown");

                string directive = TestOwnershipResolver.BuildDirective("Worker", TestOwnershipEnum.Unknown);
                AssertContains("You own the tests for this change", directive,
                    "Unknown must read as sole ownership: assuming a stage that may not exist is the defect");

                await Task.CompletedTask;
            });

            await RunTest("A pipeline definition resolves ownership when the voyage has no siblings", async () =>
            {
                Mission worker = MakeMission("Worker", 1);

                Pipeline workerOnly = new Pipeline();
                workerOnly.Stages = new List<PipelineStage> { new PipelineStage(1, "Worker") };
                AssertEqual(TestOwnershipEnum.SoleTestOwner, TestOwnershipResolver.Resolve(worker, workerOnly),
                    "WorkerOnly has no test owner but the Worker");

                Pipeline tested = new Pipeline();
                tested.Stages = new List<PipelineStage>
                {
                    new PipelineStage(1, "Worker"),
                    new PipelineStage(2, "TestEngineer"),
                    new PipelineStage(3, "Judge")
                };
                AssertEqual(TestOwnershipEnum.TestEngineerFollows, TestOwnershipResolver.Resolve(worker, tested),
                    "a Tested pipeline puts a Test Engineer after the Worker");

                AssertEqual(TestOwnershipEnum.SoleTestOwner, TestOwnershipResolver.Resolve(worker, (Pipeline?)null),
                    "an unresolvable pipeline must not promise a stage");

                await Task.CompletedTask;
            });

            await RunTest("The Judge bar follows the pipeline that actually ran", async () =>
            {
                string noTester = TestOwnershipResolver.BuildDirective("Judge", TestOwnershipEnum.SoleTestOwner);
                AssertContains("do not withhold a PASS for the absence of a separate test stage", noTester,
                    "a Judge must not fail a mission for a stage the pipeline never had");

                string withTester = TestOwnershipResolver.BuildDirective("Judge", TestOwnershipEnum.TestEngineerPreceded);
                AssertContains("A Test Engineer stage ran before you", withTester, "the Judge must review the delivered coverage");

                await Task.CompletedTask;
            });

            await RunTest("The Test Engineer is told to add gap coverage and how to escalate a defect", async () =>
            {
                string directive = TestOwnershipResolver.BuildDirective("TestEngineer", TestOwnershipEnum.TestEngineerIsMe);
                AssertContains("gap coverage, not first coverage", directive, "it must not redo the Worker's tests");
                AssertContains("commit test files only", directive, "the upstream production-code prohibition is kept");
                AssertContains("Residual Risks", directive, "a found defect must have an escalation record");

                await Task.CompletedTask;
            });

            await RunTest("A persona that neither produces nor judges gets no directive", async () =>
            {
                AssertEqual("", TestOwnershipResolver.BuildDirective("Architect", TestOwnershipEnum.SoleTestOwner),
                    "an Architect brief must not carry a testing sentence");
                // A mission dispatched with no persona still renders the worker template, so it must
                // still be told who owns tests.
                AssertContains("You own the tests for this change",
                    TestOwnershipResolver.BuildDirective(null, TestOwnershipEnum.Unknown),
                    "an absent persona defaults to Worker, matching the template default");
                AssertEqual("", TestOwnershipResolver.BuildDirective("Product Manager", TestOwnershipEnum.Unknown),
                    "a non-producing, non-judging persona must not carry a testing sentence");

                await Task.CompletedTask;
            });
        }
    }
}
