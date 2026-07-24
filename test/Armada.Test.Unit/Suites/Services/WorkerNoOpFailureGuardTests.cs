namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Guards the empty-diff false-success fix (obj_mryxzgl9). A Worker mission that lands an empty
    /// diff must be treated as a hard failure (not reconciled to Complete), while legitimately
    /// code-free personas -- Architect (emits stdout markers) and reviewer personas (Judge,
    /// TestEngineer, *Analyst) -- keep the successful no-op path.
    /// </summary>
    public class WorkerNoOpFailureGuardTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Worker No-Op Failure Guard";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("WorkerAndDefault_MustProduceChanges", () =>
            {
                AssertTrue(MissionLandingHandler.PersonaMustProduceChanges("Worker"), "Worker must produce changes");
                AssertTrue(MissionLandingHandler.PersonaMustProduceChanges("worker"), "lowercase worker must produce changes");
                AssertTrue(MissionLandingHandler.PersonaMustProduceChanges("  Worker  "), "whitespace-padded Worker must produce changes");
                AssertTrue(MissionLandingHandler.PersonaMustProduceChanges(null), "null persona defaults to Worker and must produce changes");
                AssertTrue(MissionLandingHandler.PersonaMustProduceChanges(""), "empty persona defaults to Worker and must produce changes");
            });

            await RunTest("CodeFreePersonas_AreLegitimateNoOp", () =>
            {
                AssertTrue(!MissionLandingHandler.PersonaMustProduceChanges("Architect"), "Architect emits markers; empty diff is a legitimate no-op");
                AssertTrue(!MissionLandingHandler.PersonaMustProduceChanges("Judge"), "Judge reviews; empty diff is a legitimate no-op");
                AssertTrue(!MissionLandingHandler.PersonaMustProduceChanges("TestEngineer"), "TestEngineer is a reviewer persona; empty diff is a legitimate no-op");
                AssertTrue(!MissionLandingHandler.PersonaMustProduceChanges("PortingReferenceAnalyst"), "Analyst reviews; empty diff is a legitimate no-op");
            });
        }
    }
}
