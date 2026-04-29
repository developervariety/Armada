namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Judge hybrid sub-feature #1 pin: the Suggested Follow-ups extractor on
    /// MissionService. Empty / `(none)` body returns null; non-empty body is
    /// preserved verbatim through the trim. Used by the audit-flag wiring to
    /// decide whether to surface a PASS verdict to the orchestrator's audit drain.
    /// </summary>
    public class JudgeHybridFollowUpsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Judge Hybrid -- Suggested Follow-ups";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ExtractSuggestedFollowUps_NullOrEmptyOutput_ReturnsNull", () =>
            {
                AssertNull(InvokeExtract(null));
                AssertNull(InvokeExtract(""));
                AssertNull(InvokeExtract("   "));
                return Task.CompletedTask;
            });

            await RunTest("ExtractSuggestedFollowUps_MissingSection_ReturnsNull", () =>
            {
                string output = "## Completeness\nLooks fine.\n\n## Verdict\nPASS\n[ARMADA:VERDICT] PASS\n";
                AssertNull(InvokeExtract(output));
                return Task.CompletedTask;
            });

            await RunTest("ExtractSuggestedFollowUps_NoneSentinel_ReturnsNull", () =>
            {
                string output = "## Suggested Follow-ups\n(none)\n\n## Verdict\nPASS\n";
                AssertNull(InvokeExtract(output));
                return Task.CompletedTask;
            });

            await RunTest("ExtractSuggestedFollowUps_NonEmpty_ReturnsTrimmedBody", () =>
            {
                string output =
                    "## Tests\nLooks good.\n\n" +
                    "## Suggested Follow-ups\n" +
                    "- Add Mid-tier test for the X timeout branch in Foo.cs\n" +
                    "- Doc update for the new flag in README.md\n\n" +
                    "## Verdict\nPASS\n[ARMADA:VERDICT] PASS\n";
                string? body = InvokeExtract(output);
                AssertNotNull(body);
                AssertTrue(body!.Contains("Add Mid-tier test"), "Body should contain first follow-up: " + body);
                AssertTrue(body.Contains("Doc update"), "Body should contain second follow-up: " + body);
                AssertFalse(body.StartsWith("\n"), "Body should be trimmed");
                AssertFalse(body.EndsWith("## Verdict"), "Body must stop before the next ## heading");
                return Task.CompletedTask;
            });

            await RunTest("ExtractSuggestedFollowUps_CaseInsensitiveHeading", () =>
            {
                string output = "## suggested follow-ups\n- pick this up\n## Verdict\nPASS\n";
                string? body = InvokeExtract(output);
                AssertNotNull(body);
                AssertTrue(body!.Contains("pick this up"));
                return Task.CompletedTask;
            });

            await RunTest("ExtractSuggestedFollowUps_CrlfLineEndings", () =>
            {
                string output = "## Suggested Follow-ups\r\n- one item\r\n\r\n## Verdict\r\nPASS\r\n";
                string? body = InvokeExtract(output);
                AssertNotNull(body);
                AssertTrue(body!.Contains("one item"));
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Invoke the private static MissionService.ExtractSuggestedFollowUps via
        /// reflection. The extractor is private so it stays internal-detail; tests
        /// pin the contract without forcing it onto a public surface.
        /// </summary>
        private static string? InvokeExtract(string? agentOutput)
        {
            Assembly asm = typeof(Mission).Assembly;
            Type t = asm.GetType("Armada.Core.Services.MissionService")!;
            MethodInfo mi = t.GetMethod("ExtractSuggestedFollowUps", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (string?)mi.Invoke(null, new object?[] { agentOutput });
        }
    }
}
