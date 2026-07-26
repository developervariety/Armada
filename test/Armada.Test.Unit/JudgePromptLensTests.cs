namespace Armada.Test.Unit
{
    using Armada.Core.Services;
    using Armada.Test.Common;

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
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
