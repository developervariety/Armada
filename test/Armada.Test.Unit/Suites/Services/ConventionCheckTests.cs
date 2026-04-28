namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Tests for ConventionChecker: rule coverage, line-type filtering, edge cases.</summary>
    public class ConventionCheckTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Convention Check";

        private static string DiffWithLine(string addedLine)
        {
            return "diff --git a/src/A.cs b/src/A.cs\n" +
                   "index 0000000..1111111 100644\n" +
                   "--- a/src/A.cs\n" +
                   "+++ b/src/A.cs\n" +
                   "@@ -0,0 +1,1 @@\n" +
                   "+" + addedLine + "\n";
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Check_NoViolations_Passes", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                ConventionCheckResult r = sut.Check(DiffWithLine("public class Foo {}"));
                AssertTrue(r.Passed, "Plain class declaration should not trigger any rule");
                AssertEqual(0, r.Violations.Count);
                return Task.CompletedTask;
            });

            await RunTest("Check_MockingLib_Fires_CoreRule2", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                ConventionCheckResult r = sut.Check(DiffWithLine("using Moq;"));
                AssertFalse(r.Passed);
                AssertEqual(1, r.Violations.Count);
                AssertEqual("CORE_RULE_2_mocking_lib", r.Violations[0].Rule);
                return Task.CompletedTask;
            });

            await RunTest("Check_LogInterpolation_Fires_CoreRule4", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                ConventionCheckResult r = sut.Check(DiffWithLine("    _logger.LogInformation($\"hello {name}\");"));
                AssertFalse(r.Passed);
                AssertContains("CORE_RULE_4", r.Violations[0].Rule, "Should fire CORE RULE 4");
                return Task.CompletedTask;
            });

            await RunTest("Check_PrivateKeyMarker_Fires_CoreRule5", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                ConventionCheckResult r = sut.Check(DiffWithLine("-----BEGIN RSA PRIVATE KEY-----"));
                AssertFalse(r.Passed);
                AssertContains("CORE_RULE_5", r.Violations[0].Rule);
                return Task.CompletedTask;
            });

            await RunTest("Check_SpecRef_Fires_CoreRule12", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                ConventionCheckResult r = sut.Check(DiffWithLine("// see plan §3 for rationale"));
                AssertFalse(r.Passed);
                AssertContains("CORE_RULE_12", r.Violations[0].Rule);
                return Task.CompletedTask;
            });

            await RunTest("Check_ContextLine_NotEvaluated", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                // The 'using Moq;' is on a CONTEXT line (no leading '+'). Should NOT fire.
                string diff = "diff --git a/src/A.cs b/src/A.cs\n" +
                              "@@ -1,2 +1,2 @@\n" +
                              " using Moq;\n" +  // context, not added
                              "+public class Foo {}\n";
                ConventionCheckResult r = sut.Check(diff);
                AssertTrue(r.Passed, "Context lines should not trigger rules");
                return Task.CompletedTask;
            });

            await RunTest("Check_PlusPlusPlusHeader_NotEvaluated", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                // The '+++ b/...' header starts with '+' but should be skipped.
                ConventionCheckResult r = sut.Check("+++ b/src/Moq.cs\n");
                AssertTrue(r.Passed, "+++ headers must not be evaluated as additions");
                return Task.CompletedTask;
            });

            await RunTest("Check_NullOrEmptyDiff_Passes", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                AssertTrue(sut.Check(null!).Passed);
                AssertTrue(sut.Check("").Passed);
                return Task.CompletedTask;
            });

            await RunTest("Check_MultipleViolations_AllRecorded", () =>
            {
                ConventionChecker sut = new ConventionChecker();
                string diff = "+using Moq;\n+    _logger.LogInformation($\"hi\");\n+// see plan\n";
                ConventionCheckResult r = sut.Check(diff);
                AssertFalse(r.Passed);
                AssertTrue(r.Violations.Count >= 3, "Should record all 3 violations");
                return Task.CompletedTask;
            });
        }
    }
}
