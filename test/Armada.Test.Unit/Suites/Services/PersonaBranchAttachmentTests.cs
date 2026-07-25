namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Guards the downstream-persona dock race. Git allows only one worktree to hold a branch, so a
    /// stage that provisions while an earlier stage still holds the shared mission branch fails with
    /// git exit 128. Personas that never commit run detached at the same commit instead; personas
    /// that commit must stay attached or their commits would be orphaned on a detached HEAD.
    /// Same-stage fan-out (dual-Judge) depends on the read-only personas detaching.
    /// </summary>
    public class PersonaBranchAttachmentTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Persona Branch Attachment";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CommittingPersonas_RequireAttachment", () =>
            {
                AssertTrue(MissionService.PersonaRequiresBranchAttachment("Worker"),
                    "Worker commits its implementation and must hold the branch");
                AssertTrue(MissionService.PersonaRequiresBranchAttachment("TestEngineer"),
                    "TestEngineer commits tests and must hold the branch");
                AssertTrue(MissionService.PersonaRequiresBranchAttachment("MemoryConsolidator"),
                    "MemoryConsolidator writes playbook updates and must hold the branch");
            });

            await RunTest("ReadOnlyPersonas_DoNotRequireAttachment", () =>
            {
                AssertTrue(!MissionService.PersonaRequiresBranchAttachment("Judge"),
                    "Judge is final review only, so it can run detached alongside the branch holder");
                AssertTrue(!MissionService.PersonaRequiresBranchAttachment("Architect"),
                    "Architect decomposes read-only");
                AssertTrue(!MissionService.PersonaRequiresBranchAttachment("PortingReferenceAnalyst"),
                    "specialist reviewers read the diff rather than commit");
                AssertTrue(!MissionService.PersonaRequiresBranchAttachment("TenantSecurityReviewer"),
                    "specialist reviewers read the diff rather than commit");
            });

            await RunTest("PersonaMatching_IsCaseAndWhitespaceInsensitive", () =>
            {
                AssertTrue(!MissionService.PersonaRequiresBranchAttachment("  judge  "),
                    "persona matching must tolerate casing and padding");
                AssertTrue(!MissionService.PersonaRequiresBranchAttachment("PRODUCT MANAGER"),
                    "multi-word personas must match case-insensitively");
            });

            await RunTest("UnknownPersonas_DefaultToAttached", () =>
            {
                // Detaching a committing persona would orphan its commits, so anything unrecognized
                // keeps today's attached behavior.
                AssertTrue(MissionService.PersonaRequiresBranchAttachment(null),
                    "a null persona must default to attached");
                AssertTrue(MissionService.PersonaRequiresBranchAttachment(""),
                    "an empty persona must default to attached");
                AssertTrue(MissionService.PersonaRequiresBranchAttachment("   "),
                    "a blank persona must default to attached");
                AssertTrue(MissionService.PersonaRequiresBranchAttachment("SomeFuturePersona"),
                    "an unrecognized persona must default to attached rather than risk orphaned commits");
            });
        }
    }
}
