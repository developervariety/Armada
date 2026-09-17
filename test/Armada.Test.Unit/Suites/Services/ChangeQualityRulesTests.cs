namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the deterministic change_quality backing: a clean diff yields no weakness, a Slop FAIL
    /// finding yields a core_rule MustFix, and deep added nesting or a long added run yields a
    /// cognitive_complexity MustFix. These are the authoritative verdicts the model may only add to.
    /// </summary>
    public class ChangeQualityRulesTests : TestSuite
    {
        public override string Name => "Change Quality Rules";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Clean diff yields no deterministic weakness", () =>
            {
                string diff = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,2 @@\n public class A { }\n+// a harmless added comment\n";
                IReadOnlyList<ChangeQualityWeakness> w = ChangeQualityRules.Evaluate(diff, false);
                AssertEqual(0, w.Count, "a benign change has no deterministic weakness");
            });

            await RunTest("A Slop FAIL finding yields a core_rule MustFix", () =>
            {
                // A project-wide NoWarn is a Slop FAIL rule.
                string diff = "diff --git a/src/A.csproj b/src/A.csproj\n--- a/src/A.csproj\n+++ b/src/A.csproj\n@@ -1,1 +1,2 @@\n <Project>\n+  <PropertyGroup><NoWarn>CS1591</NoWarn></PropertyGroup>\n";
                IReadOnlyList<ChangeQualityWeakness> w = ChangeQualityRules.Evaluate(diff, false);
                ChangeQualityWeakness? core = w.FirstOrDefault(x => x.Dimension == ChangeQualityDimensions.CoreRule);
                AssertNotNull(core, "a slop FAIL finding produces a core_rule weakness");
                AssertEqual(ChangeQualitySeverity.MustFix, core!.Severity);
                AssertEqual(ChangeQualitySource.Rule, core.Source);
            });

            await RunTest("Deep added nesting yields a cognitive_complexity MustFix", () =>
            {
                System.Text.StringBuilder b = new System.Text.StringBuilder();
                b.Append("diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,20 @@\n");
                b.Append("+public class A {\n");
                for (int i = 0; i < ChangeQualityRules.ComplexityNestingThreshold + 1; i++) b.Append("+  if (x) {\n");
                b.Append("+  DoWork();\n");
                for (int i = 0; i < ChangeQualityRules.ComplexityNestingThreshold + 1; i++) b.Append("+  }\n");
                b.Append("+}\n");
                IReadOnlyList<ChangeQualityWeakness> w = ChangeQualityRules.Evaluate(b.ToString(), false);
                ChangeQualityWeakness? cx = w.FirstOrDefault(x => x.Dimension == ChangeQualityDimensions.CognitiveComplexity);
                AssertNotNull(cx, "deeply nested added code flags cognitive_complexity");
                AssertEqual(ChangeQualitySeverity.MustFix, cx!.Severity);
                AssertEqual(ChangeQualitySource.Rule, cx.Source);
            });

            await RunTest("MeasureComplexity ignores removed and header lines", () =>
            {
                string diff = "diff --git a/x b/x\n@@ -1,3 +1,1 @@\n-  if (a) { if (b) { if (c) { deep(); } } }\n+shallow();\n";
                ChangeQualityRules.ComplexityMetric m = ChangeQualityRules.MeasureComplexity(diff);
                AssertEqual(0, m.MaxAddedNesting, "removed lines do not count toward added nesting");
            });
        }
    }
}
