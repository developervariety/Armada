namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Guards the empty-playbook staging gate: a playbook that has never had an accepted
    /// reflection carries only a heading plus a placeholder line, yet was still materialized
    /// and referenced in every mission's instructions -- costing the captain a read to learn
    /// nothing. Substantive playbooks must keep flowing through untouched.
    /// </summary>
    public class PlaybookContentGateTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Playbook Content Gate";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("PlaceholderPlaybooks_AreNotSubstantive", () =>
            {
                // The exact persona-worker-learned.md body observed in staged missions (72 chars).
                AssertTrue(!MissionService.HasSubstantivePlaybookContent(
                    "# Persona Learned Notes -- Worker\n\nNo accepted persona-curate notes yet.\n"),
                    "a heading plus the persona placeholder is not substantive");

                AssertTrue(!MissionService.HasSubstantivePlaybookContent(
                    "# Vessel Learned Facts\n\nNo accepted reflection notes yet."),
                    "a heading plus the vessel placeholder is not substantive");

                AssertTrue(!MissionService.HasSubstantivePlaybookContent("no accepted persona-curate notes yet"),
                    "placeholder matching is case- and trailing-period-insensitive");
            });

            await RunTest("EmptyAndStructureOnlyPlaybooks_AreNotSubstantive", () =>
            {
                AssertTrue(!MissionService.HasSubstantivePlaybookContent(null), "null is not substantive");
                AssertTrue(!MissionService.HasSubstantivePlaybookContent(""), "empty is not substantive");
                AssertTrue(!MissionService.HasSubstantivePlaybookContent("   \n\t\n  "), "whitespace is not substantive");
                AssertTrue(!MissionService.HasSubstantivePlaybookContent("# Title\n## Section\n### Subsection\n"),
                    "headings alone are not substantive");
                AssertTrue(!MissionService.HasSubstantivePlaybookContent("# Title\n\n---\n\n"),
                    "headings plus a horizontal rule are not substantive");
            });

            await RunTest("RealPlaybooks_AreSubstantive", () =>
            {
                // Shape of the real vessel learned-facts playbook: heading + actionable bullets.
                AssertTrue(MissionService.HasSubstantivePlaybookContent(
                    "# Vessel Learned Facts\n## Static extraction boundaries\n" +
                    "- Keep the main Diesel extractor pipeline static-only.\n"),
                    "a heading plus a real bullet is substantive");

                AssertTrue(MissionService.HasSubstantivePlaybookContent("MethodName always requires exact match."),
                    "a bare instruction line is substantive");

                AssertTrue(MissionService.HasSubstantivePlaybookContent(
                    "# Notes\n\nNo accepted persona-curate notes yet.\n\n- But this rule was added later.\n"),
                    "a placeholder does not suppress real content that follows it");
            });

            await RunTest("PlaceholderLookalikes_StaySubstantive", () =>
            {
                AssertTrue(MissionService.HasSubstantivePlaybookContent("No accepted seed-key vectors may ship without source bytes."),
                    "a real rule starting with 'No accepted' but not ending in 'yet' is substantive");

                AssertTrue(MissionService.HasSubstantivePlaybookContent("Do not accept synthetic vectors yet to be verified."),
                    "an instruction containing 'yet' mid-sentence is substantive");
            });
        }
    }
}
