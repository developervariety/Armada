namespace Armada.Test.Unit
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Verifies the anti-Goodhart Judge guidance: every Judge prompt carries the three distinct review
    /// lenses (correctness / safety-blast-radius / source-fidelity) and the bounded-judge rule (block only
    /// on a real, corpus-present affected case), and non-Judge personas do not carry it.
    /// </summary>
    public sealed class JudgePromptLensTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "JudgePromptLens";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("JudgeContract_CarriesThreeLensesAndBoundedRule", () =>
            {
                string judge = MissionPromptBuilder.GetPersonaOutputContract("Judge");
                AssertTrue(judge.Contains("CORRECTNESS"), "judge prompt names the correctness lens");
                AssertTrue(judge.Contains("SAFETY & BLAST-RADIUS"), "judge prompt names the safety/blast-radius lens");
                AssertTrue(judge.Contains("SOURCE-FIDELITY"), "judge prompt names the source-fidelity lens");
                AssertTrue(judge.Contains("corpus-present affected case"), "judge prompt carries the bounded-judge rule");
                AssertTrue(judge.Contains("NOT a blocker"), "judge prompt says hypotheticals are not blockers");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("NonJudgePersonas_DoNotCarryJudgeGuidance", () =>
            {
                AssertTrue(!MissionPromptBuilder.GetPersonaOutputContract("Worker").Contains("BOUNDED-JUDGE"),
                    "worker prompt must not carry judge guidance");
                AssertTrue(!MissionPromptBuilder.GetPersonaOutputContract("TestEngineer").Contains("BOUNDED-JUDGE"),
                    "test-engineer prompt must not carry judge guidance");
                AssertTrue(!MissionPromptBuilder.GetPersonaOutputContract("Worker").Contains("DELIVERY-EVIDENCE"),
                    "worker prompt must not carry the delivery-evidence rule");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // A Judge once read symbols at the tip, found them real and corroborated, and passed an
            // acceptance item the branch had never touched: the symbols were already on the base.
            // Delivery is a property of the diff, so the contract must say so and demand hunks.
            await RunTest("JudgeContract_ProvesDeliveryByTheDiffNotTheTip", () =>
            {
                string judge = MissionPromptBuilder.GetPersonaOutputContract("Judge");
                AssertTrue(judge.Contains("DELIVERY-EVIDENCE RULE"), "judge prompt names the delivery-evidence rule");
                AssertTrue(judge.Contains("DIFF against its base"), "judge prompt says delivery is proven by the diff");
                AssertTrue(judge.Contains("cite the diff hunk"), "judge prompt demands a diff hunk per acceptance item");
                AssertTrue(judge.Contains("NOT DELIVERED"), "judge prompt gives the wording for an undelivered item");
                AssertTrue(judge.Contains("Never cite `git grep`"), "judge prompt forbids tip presence as delivery evidence");
                AssertTrue(judge.Contains("Failed Check exists at the reviewed tip"), "judge prompt ties a failed check to its acceptance item");
                AssertTrue(judge.Contains("merge-base --is-ancestor"), "judge prompt requires accepted work to be an ancestor of the tip");
                AssertTrue(judge.Contains("ABSENT from this base"), "judge prompt names absence of accepted work as a regression");
                AssertTrue(judge.Contains("cherry-picked forward"), "judge prompt accepts cherry-picked presence in the diff as the alternative to ancestry");

                string withLens = MissionPromptBuilder.BuildJudgeLensDirective("CORRECTNESS");
                AssertTrue(withLens.Contains("DELIVERY-EVIDENCE RULE"), "the primary-lens directive keeps the delivery-evidence rule");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // Perspective-diverse pool: a Judge with an assigned primary lens
            // leads with that lens and keeps the bounded rule; a Judge without one keeps the
            // combined three-lens instruction.
            await RunTest("JudgeWithPrimaryLens_LeadsWithItAndKeepsBoundedRule", () =>
            {
                string directive = MissionPromptBuilder.BuildJudgeLensDirective("CORRECTNESS");
                AssertTrue(directive.Contains("PRIMARY lens for this review is CORRECTNESS"),
                    "the directive must name the assigned primary lens");
                AssertTrue(directive.Contains("corpus-present affected case"),
                    "the primary-lens directive must keep the bounded-judge rule");

                string combined = MissionPromptBuilder.BuildJudgeLensDirective(null);
                AssertTrue(combined.Contains("THREE distinct lenses"),
                    "a Judge without an assigned lens keeps the combined three-lens instruction");

                string contract = MissionPromptBuilder.GetPersonaOutputContract("Judge", Armada.Core.Enums.MissionModeEnum.Implementation, "SOURCE-FIDELITY");
                AssertTrue(contract.Contains("PRIMARY lens for this review is SOURCE-FIDELITY"),
                    "the Judge output contract must embed the assigned primary lens");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // Perspective-diverse pool: parallel Judges on one voyage and
            // stage get DISTINCT primary lenses, a solo Judge gets none, and the assignment is
            // recorded as a mission.judge_lens event.
            await RunTest("ParallelJudges_GetDistinctLenses_RecordedAsEvents", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();
                settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_lens_docks_" + Guid.NewGuid().ToString("N"));
                settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_lens_repos_" + Guid.NewGuid().ToString("N"));
                StubGitService git = new StubGitService();
                IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                MissionService svc = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);

                Voyage voyage = new Voyage("lens-voyage");
                voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                Mission judgeA = new Mission("[Judge] A", "review");
                judgeA.VoyageId = voyage.Id;
                judgeA.Persona = "Judge";
                judgeA.StageOrder = 2;
                await testDb.Driver.Missions.CreateAsync(judgeA).ConfigureAwait(false);

                Mission judgeB = new Mission("[Judge] B", "review");
                judgeB.VoyageId = voyage.Id;
                judgeB.Persona = "Judge";
                judgeB.StageOrder = 2;
                await testDb.Driver.Missions.CreateAsync(judgeB).ConfigureAwait(false);

                Mission soloJudge = new Mission("[Judge] C", "review");
                soloJudge.VoyageId = voyage.Id;
                soloJudge.Persona = "Judge";
                soloJudge.StageOrder = 4;
                await testDb.Driver.Missions.CreateAsync(soloJudge).ConfigureAwait(false);

                string? lensA = await svc.ResolveJudgeLensAsync(judgeA, CancellationToken.None).ConfigureAwait(false);
                string? lensB = await svc.ResolveJudgeLensAsync(judgeB, CancellationToken.None).ConfigureAwait(false);
                string? lensSolo = await svc.ResolveJudgeLensAsync(soloJudge, CancellationToken.None).ConfigureAwait(false);

                AssertNotNull(lensA, "Parallel Judge A must receive a primary lens.");
                AssertNotNull(lensB, "Parallel Judge B must receive a primary lens.");
                AssertTrue(!String.Equals(lensA, lensB, StringComparison.Ordinal), "Parallel Judges must receive DISTINCT primary lenses.");
                AssertTrue(MissionPromptBuilder.JudgeLensNames.Contains(lensA!), "The lens must be one of the canonical lenses.");
                AssertTrue(MissionPromptBuilder.JudgeLensNames.Contains(lensB!), "The lens must be one of the canonical lenses.");
                AssertNull(lensSolo, "A solo Judge keeps the combined instruction and receives no primary lens.");

                EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                AssertTrue(events.Objects.Any(e => e.EventType == "mission.judge_lens" && e.MissionId == judgeA.Id), "Judge A's lens assignment must be recorded as an event.");
                AssertTrue(events.Objects.Any(e => e.EventType == "mission.judge_lens" && e.MissionId == judgeB.Id), "Judge B's lens assignment must be recorded as an event.");
                AssertTrue(!events.Objects.Any(e => e.EventType == "mission.judge_lens" && e.MissionId == soloJudge.Id), "A solo Judge must not record a lens event.");
            }).ConfigureAwait(false);
        }
    }
}
