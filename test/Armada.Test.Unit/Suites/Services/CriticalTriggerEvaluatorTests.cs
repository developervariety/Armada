namespace Armada.Test.Unit.Suites.Services
{
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Tests for CriticalTriggerEvaluator: path keywords, content patterns, convention passthrough, size threshold.</summary>
    public class CriticalTriggerEvaluatorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Critical Trigger Evaluator";

        private static string BuildDiff(string[] paths, int[] addedCounts)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < paths.Length; i++)
            {
                sb.AppendLine("diff --git a/" + paths[i] + " b/" + paths[i]);
                sb.AppendLine("index 0000000..1111111 100644");
                sb.AppendLine("--- a/" + paths[i]);
                sb.AppendLine("+++ b/" + paths[i]);
                sb.AppendLine("@@ -0,0 +1," + addedCounts[i] + " @@");
                for (int j = 0; j < addedCounts[i]; j++) sb.AppendLine("+line " + j);
            }
            return sb.ToString();
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Evaluate_BenignDiff_DoesNotFire", () =>
            {
                CriticalTriggerEvaluator sut = new CriticalTriggerEvaluator();
                CriticalTriggerResult r = sut.Evaluate(
                    BuildDiff(new string[] { "src/Features/Fleet/Foo.cs" }, new int[] { 5 }),
                    new ConventionCheckResult { Passed = true });
                AssertFalse(r.Fired);
                AssertEqual(0, r.TriggeredCriteria.Count);
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_PathContainsAuth_FiresPathCriterion", () =>
            {
                CriticalTriggerEvaluator sut = new CriticalTriggerEvaluator();
                CriticalTriggerResult r = sut.Evaluate(
                    BuildDiff(new string[] { "src/FleetPortal.Web/Components/AuthLayout.razor" }, new int[] { 5 }),
                    new ConventionCheckResult { Passed = true });
                AssertTrue(r.Fired);
                AssertContains("path", string.Join(",", r.TriggeredCriteria));
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_HasQueryFilterContent_FiresContentCriterion", () =>
            {
                CriticalTriggerEvaluator sut = new CriticalTriggerEvaluator();
                string diff = "+ entity.HasQueryFilter(x => x.FleetId == _ctx.FleetId);\n";
                CriticalTriggerResult r = sut.Evaluate(diff, new ConventionCheckResult { Passed = true });
                AssertTrue(r.Fired);
                AssertContains("content", string.Join(",", r.TriggeredCriteria));
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_ConventionFailed_FiresConventionCriterion", () =>
            {
                CriticalTriggerEvaluator sut = new CriticalTriggerEvaluator();
                CriticalTriggerResult r = sut.Evaluate(
                    BuildDiff(new string[] { "src/A.cs" }, new int[] { 5 }),
                    new ConventionCheckResult { Passed = false });
                AssertTrue(r.Fired);
                AssertContains("convention", string.Join(",", r.TriggeredCriteria));
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_LargeDiff_FiresSizeCriterion", () =>
            {
                CriticalTriggerEvaluator sut = new CriticalTriggerEvaluator();
                CriticalTriggerResult r = sut.Evaluate(
                    BuildDiff(new string[] { "src/A.cs" }, new int[] { 100 }),
                    new ConventionCheckResult { Passed = true });
                AssertTrue(r.Fired);
                AssertContains("size", string.Join(",", r.TriggeredCriteria));
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_ManyFiles_FiresSizeCriterion", () =>
            {
                CriticalTriggerEvaluator sut = new CriticalTriggerEvaluator();
                CriticalTriggerResult r = sut.Evaluate(
                    BuildDiff(new string[] { "src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs", "src/E.cs" }, new int[] { 1, 1, 1, 1, 1 }),
                    new ConventionCheckResult { Passed = true });
                AssertTrue(r.Fired);
                AssertContains("size", string.Join(",", r.TriggeredCriteria));
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_MultipleCriteria_AllRecorded", () =>
            {
                CriticalTriggerEvaluator sut = new CriticalTriggerEvaluator();
                CriticalTriggerResult r = sut.Evaluate(
                    BuildDiff(new string[] { "src/Features/Auth/AuthService.cs" }, new int[] { 100 }),
                    new ConventionCheckResult { Passed = false });
                AssertTrue(r.Fired);
                AssertContains("path", string.Join(",", r.TriggeredCriteria));
                AssertContains("convention", string.Join(",", r.TriggeredCriteria));
                AssertContains("size", string.Join(",", r.TriggeredCriteria));
                return Task.CompletedTask;
            });

            await RunTest("Evaluate_PathKeywordCaseInsensitive_Fires", () =>
            {
                CriticalTriggerEvaluator sut = new CriticalTriggerEvaluator();
                CriticalTriggerResult r = sut.Evaluate(
                    BuildDiff(new string[] { "src/MyAuthHelper.cs" }, new int[] { 5 }),
                    new ConventionCheckResult { Passed = true });
                AssertTrue(r.Fired, "Should match 'Auth' substring case-insensitively");
                return Task.CompletedTask;
            });
        }
    }
}
