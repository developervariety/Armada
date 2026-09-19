namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The synthetic evaluation set for the typed decisions that gate by default. Every case builds its
    /// request through the decision's own adapter, so it tests the exact state shape and questions
    /// production sends. Reference cases change one relevant fact between the variants, so the
    /// reference answer changes with it; consistency cases change one fact that must not matter. The
    /// state is synthetic and carries no operational record.
    /// </summary>
    public static class TypedDecisionEvalCatalog
    {
        #region Public-Methods

        /// <summary>
        /// Build the case set.
        /// </summary>
        /// <param name="recorder">A recorder, required by the adapters; building a case records nothing.</param>
        /// <param name="settings">Typed-decision settings, for the state budget.</param>
        /// <param name="logging">Logging module.</param>
        /// <returns>The cases, in a stable order.</returns>
        public static List<TypedDecisionEvalCase> Build(TypedDecisionRecorder recorder, TypedDecisionSettings settings, LoggingModule logging)
        {
            if (recorder == null) throw new ArgumentNullException(nameof(recorder));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logging == null) throw new ArgumentNullException(nameof(logging));

            ITypedDecisionClient silent = new NullTypedDecisionClient();
            List<TypedDecisionEvalCase> cases = new List<TypedDecisionEvalCase>();
            AddFailureCause(cases, new TypedFailureCauseAdapter(silent, recorder, settings, logging));
            AddRefusal(cases, new TypedRefusalAdapter(silent, recorder, settings, logging));
            AddRuntimeFailure(cases, new TypedRuntimeFailureAdapter(silent, recorder, settings, logging));
            AddReviewSubstance(cases, new TypedReviewSubstanceAdapter(silent, recorder, settings, logging));
            AddLintFinding(cases, new TypedLintFindingAdapter(silent, recorder, settings, logging));
            AddChangeQuality(cases, new TypedChangeQualityAdapter(silent, recorder, settings, logging));
            return cases;
        }

        #endregion

        #region Private-Methods

        private static void AddFailureCause(List<TypedDecisionEvalCase> cases, TypedFailureCauseAdapter adapter)
        {
            FailureCauseDecisionInput hostFault = new FailureCauseDecisionInput
            {
                Mission = EvalMission("Add a checksum to the frame encoder", "Worker"),
                FailureReason = "Build Check failed before compilation started.",
                AgentOutputTail = "Implemented the checksum and its unit tests. Handing off to the build.",
                MissionMode = "Code",
                Checks = new List<FailureCauseCheckFact>
                {
                    new FailureCauseCheckFact
                    {
                        Label = "Build",
                        Type = "Build",
                        Status = "Failed",
                        CommitMatchesJudge = true,
                        ExitCode = 1,
                        Diagnostics = "error: could not write to the output directory: No space left on device"
                    }
                }
            };
            FailureCauseDecisionInput workDefect = new FailureCauseDecisionInput
            {
                Mission = EvalMission("Add a checksum to the frame encoder", "Worker"),
                FailureReason = "Build Check failed with compiler errors.",
                AgentOutputTail = "Implemented the checksum and its unit tests. Handing off to the build.",
                MissionMode = "Code",
                Checks = new List<FailureCauseCheckFact>
                {
                    new FailureCauseCheckFact
                    {
                        Label = "Build",
                        Type = "Build",
                        Status = "Failed",
                        CommitMatchesJudge = true,
                        ExitCode = 1,
                        Diagnostics = "FrameEncoder.cs(42,17): error CS0103: The name 'checksumSeed' does not exist in the current context"
                    }
                }
            };

            cases.Add(Pair(
                "failure_cause.host_fault_vs_compile_error",
                "failure_cause",
                TypedDecisionEvalCaseKindEnum.Reference,
                "A full disk is a host fault no captain can repair; a compiler error in the mission's own file is a work defect.",
                adapter.DescribeRequest(hostFault),
                adapter.DescribeRequest(workDefect),
                Expect("cause", new TypedDecisionExpectation { Choice = "environmental" }),
                Expect("cause", new TypedDecisionExpectation { Choice = "work_defect" })));

            FailureCauseDecisionInput otherPersona = new FailureCauseDecisionInput
            {
                Mission = EvalMission("Add a checksum to the frame encoder", "TestEngineer"),
                FailureReason = workDefect.FailureReason,
                AgentOutputTail = workDefect.AgentOutputTail,
                MissionMode = workDefect.MissionMode,
                Checks = workDefect.Checks
            };
            cases.Add(Consistency(
                "failure_cause.persona_does_not_change_cause",
                "failure_cause",
                "The stage persona does not change what caused a compiler error.",
                adapter.DescribeRequest(workDefect),
                adapter.DescribeRequest(otherPersona),
                "cause"));
        }

        private static void AddRefusal(List<TypedDecisionEvalCase> cases, TypedRefusalAdapter adapter)
        {
            RefusalDecisionInput refused = new RefusalDecisionInput
            {
                Mission = EvalMission("Implement the token refresh handler", "Worker"),
                MissionTitle = "Implement the token refresh handler",
                AgentOutputTail = "I can't help with this. Implementing this handler could be used to bypass access controls, so I have not made any changes.",
                MarkerPresent = false
            };
            RefusalDecisionInput quoted = new RefusalDecisionInput
            {
                Mission = EvalMission("Implement the token refresh handler", "Worker"),
                MissionTitle = "Implement the token refresh handler",
                AgentOutputTail = "The brief notes that an earlier run replied \"I can't help with this\"; that was a mistake. "
                    + "I implemented the refresh request and response handling, added four unit tests, and all tests pass (0 failed).",
                MarkerPresent = false
            };

            cases.Add(Pair(
                "refusal.own_refusal_vs_quoted_phrase",
                "refusal",
                TypedDecisionEvalCaseKindEnum.Reference,
                "The first captain declines in its own words; the second quotes an earlier refusal and reports the work done.",
                adapter.DescribeRequest(refused),
                adapter.DescribeRequest(quoted),
                Expect("outcome", new TypedDecisionExpectation { Choice = "refused_policy" }),
                Expect(
                    "outcome", new TypedDecisionExpectation { Choice = "completed" },
                    "quoted_not_own", new TypedDecisionExpectation { NoulAtLeast = 0.6 })));
        }

        private static void AddRuntimeFailure(List<TypedDecisionEvalCase> cases, TypedRuntimeFailureAdapter adapter)
        {
            RuntimeFailureDecisionInput throttled = new RuntimeFailureDecisionInput
            {
                Mission = EvalMission("Refactor the retry policy", "Worker"),
                ExitCode = 1,
                Runtime = "ClaudeCode",
                Tail = "API Error: 429 Too Many Requests. You have exceeded the rate limit for this organization. Retry after 60 seconds."
            };
            RuntimeFailureDecisionInput crashed = new RuntimeFailureDecisionInput
            {
                Mission = EvalMission("Refactor the retry policy", "Worker"),
                ExitCode = 139,
                Runtime = "ClaudeCode",
                Tail = "Segmentation fault (core dumped)"
            };

            cases.Add(Pair(
                "runtime_failure.rate_limit_vs_crash",
                "runtime_failure",
                TypedDecisionEvalCaseKindEnum.Reference,
                "A 429 rate-limit message is a usage limit; a segmentation fault with no provider message is a crash.",
                adapter.DescribeRequest(throttled),
                adapter.DescribeRequest(crashed),
                Expect("kind", new TypedDecisionExpectation { Choice = "usage_limit" }),
                Expect("kind", new TypedDecisionExpectation { Choice = "crash" })));

            RuntimeFailureDecisionInput otherRuntime = new RuntimeFailureDecisionInput
            {
                Mission = throttled.Mission,
                ExitCode = throttled.ExitCode,
                Runtime = "Codex",
                Tail = throttled.Tail
            };
            cases.Add(Consistency(
                "runtime_failure.runtime_name_does_not_change_kind",
                "runtime_failure",
                "The runtime that printed a rate-limit message does not change that it is a usage limit.",
                adapter.DescribeRequest(throttled),
                adapter.DescribeRequest(otherRuntime),
                "kind"));
        }

        private static void AddReviewSubstance(List<TypedDecisionEvalCase> cases, TypedReviewSubstanceAdapter adapter)
        {
            List<string> sections = new List<string> { "Evidence", "Failure Modes", "Residual Risks", "Verdict" };
            ReviewSubstanceDecisionInput substantive = new ReviewSubstanceDecisionInput
            {
                Mission = EvalMission("Review: add a checksum to the frame encoder", "Judge"),
                RequiredSections = sections,
                DiffStat = "3 files changed, 64 insertions(+), 2 deletions(-)",
                CheckSummary = "Build: Passed (exit 0). UnitTest: Passed, 212 total, 0 failed.",
                Narrative = "## Evidence\nFrameEncoder.Encode now appends a CRC-16 over the payload; FrameEncoderTests adds four cases, "
                    + "including an empty payload and a 255-byte payload, and the UnitTest Check passed with 0 failed.\n"
                    + "## Failure Modes\nA payload longer than 255 bytes is rejected before the checksum runs; the new test covers the rejection.\n"
                    + "## Residual Risks\nThe decoder side is unchanged, so a peer still on the old format will reject new frames until it is updated.\n"
                    + "## Verdict\nPASS"
            };
            ReviewSubstanceDecisionInput headingsOnly = new ReviewSubstanceDecisionInput
            {
                Mission = substantive.Mission,
                RequiredSections = sections,
                DiffStat = substantive.DiffStat,
                CheckSummary = substantive.CheckSummary,
                Narrative = "## Evidence\nLooks good.\n## Failure Modes\nNone.\n## Residual Risks\nNone.\n## Verdict\nPASS"
            };

            cases.Add(Pair(
                "review_substance.evidenced_vs_headings_only",
                "review_substance",
                TypedDecisionEvalCaseKindEnum.Reference,
                "The first review cites the change, the tests, and a real residual risk; the second only names the headings. "
                    + "The decision holds a thin PASS when the probability of the two lowest levels reaches its threshold (0.85).",
                adapter.DescribeRequest(substantive),
                adapter.DescribeRequest(headingsOnly),
                Expect(
                    "substantiated", new TypedDecisionExpectation { ScoreAtLeast = 2.0 },
                    "section_1", new TypedDecisionExpectation { NoulAtLeast = 0.6 }),
                Expect(
                    "substantiated", new TypedDecisionExpectation { ScoreLevelsUpTo = 1, ScoreLevelsProbabilityAtLeast = 0.85 },
                    "section_1", new TypedDecisionExpectation { NoulAtMost = 0.4 })));
        }

        private static void AddLintFinding(List<TypedDecisionEvalCase> cases, TypedLintFindingAdapter adapter)
        {
            LintFindingDecisionInput correctness = new LintFindingDecisionInput
            {
                Mission = EvalMission("Lint: frame encoder", "Linter"),
                Findings = new List<string>
                {
                    "FrameEncoder.Encode reads buffer[length] after the loop, one element past the end; a full 255-byte frame throws IndexOutOfRangeException."
                }
            };
            LintFindingDecisionInput style = new LintFindingDecisionInput
            {
                Mission = EvalMission("Lint: frame encoder", "Linter"),
                Findings = new List<string>
                {
                    "FrameEncoder.Encode names a local 'tmp'; 'scratch' would read slightly better. Behaviour is unaffected."
                }
            };

            cases.Add(Pair(
                "lint_finding.out_of_bounds_vs_naming_taste",
                "lint_finding",
                TypedDecisionEvalCaseKindEnum.Reference,
                "An out-of-bounds read that throws on a full frame is a correctness defect; a local variable name is a style preference.",
                adapter.DescribeRequest(correctness),
                adapter.DescribeRequest(style),
                Expect(
                    "class_1", new TypedDecisionExpectation { Choice = "correctness" },
                    "severity_1", new TypedDecisionExpectation { ScoreAtLeast = 2.0 }),
                Expect("class_1", new TypedDecisionExpectation { Choice = "style_preference" })));
        }

        private static void AddChangeQuality(List<TypedDecisionEvalCase> cases, TypedChangeQualityAdapter adapter)
        {
            // Reference: blatant duplication vs a clean extraction -> dry_weak high then low.
            ChangeQualityInput duplicated = new ChangeQualityInput
            {
                UnifiedDiff = "diff --git a/src/Orders.cs b/src/Orders.cs\n--- a/src/Orders.cs\n+++ b/src/Orders.cs\n@@ -1,1 +1,12 @@\n"
                    + "+    public decimal TotalWithTax(List<Item> items)\n+    {\n+        decimal sum = 0;\n+        foreach (Item i in items) sum += i.Price * i.Quantity;\n+        return sum * 1.08m;\n+    }\n"
                    + "+    public decimal TotalWithDiscount(List<Item> items)\n+    {\n+        decimal sum = 0;\n+        foreach (Item i in items) sum += i.Price * i.Quantity;\n+        return sum * 0.9m;\n+    }\n"
            };
            ChangeQualityInput extracted = new ChangeQualityInput
            {
                UnifiedDiff = "diff --git a/src/Orders.cs b/src/Orders.cs\n--- a/src/Orders.cs\n+++ b/src/Orders.cs\n@@ -1,1 +1,10 @@\n"
                    + "+    private decimal Subtotal(List<Item> items)\n+    {\n+        decimal sum = 0;\n+        foreach (Item i in items) sum += i.Price * i.Quantity;\n+        return sum;\n+    }\n"
                    + "+    public decimal TotalWithTax(List<Item> items) => Subtotal(items) * 1.08m;\n+    public decimal TotalWithDiscount(List<Item> items) => Subtotal(items) * 0.9m;\n"
            };
            cases.Add(Pair(
                "change_quality.duplication_vs_extraction",
                "change_quality",
                TypedDecisionEvalCaseKindEnum.Reference,
                "Two methods with an identical loop body violate DRY; extracting the shared subtotal does not.",
                adapter.DescribeRequest(duplicated),
                adapter.DescribeRequest(extracted),
                new Dictionary<string, TypedDecisionExpectation>(StringComparer.Ordinal)
                {
                    ["dry_weak"] = new TypedDecisionExpectation { NoulAtLeast = 0.6 }
                },
                new Dictionary<string, TypedDecisionExpectation>(StringComparer.Ordinal)
                {
                    ["dry_weak"] = new TypedDecisionExpectation { NoulAtMost = 0.4 }
                }));

            // Reference: deep nesting vs a flat rewrite -> cognitive_complexity_weak high then low.
            ChangeQualityInput nested = new ChangeQualityInput
            {
                UnifiedDiff = "diff --git a/src/Classify.cs b/src/Classify.cs\n--- a/src/Classify.cs\n+++ b/src/Classify.cs\n@@ -1,1 +1,16 @@\n"
                    + "+    public string Classify(int a, int b, int c)\n+    {\n+        if (a > 0)\n+        {\n+            if (b > 0)\n+            {\n+                if (c > 0)\n+                {\n+                    if (a > b)\n+                    {\n+                        if (b > c) return \"descending\";\n+                    }\n+                }\n+            }\n+        }\n+        return \"other\";\n+    }\n"
            };
            ChangeQualityInput flat = new ChangeQualityInput
            {
                UnifiedDiff = "diff --git a/src/Classify.cs b/src/Classify.cs\n--- a/src/Classify.cs\n+++ b/src/Classify.cs\n@@ -1,1 +1,6 @@\n"
                    + "+    public string Classify(int a, int b, int c)\n+    {\n+        if (a <= 0 || b <= 0 || c <= 0) return \"other\";\n+        return a > b && b > c ? \"descending\" : \"other\";\n+    }\n"
            };
            cases.Add(Pair(
                "change_quality.deep_nesting_vs_flat",
                "change_quality",
                TypedDecisionEvalCaseKindEnum.Reference,
                "Five levels of nested ifs is more cognitively complex than the flat guard-clause rewrite of the same logic.",
                adapter.DescribeRequest(nested),
                adapter.DescribeRequest(flat),
                new Dictionary<string, TypedDecisionExpectation>(StringComparer.Ordinal)
                {
                    ["cognitive_complexity_weak"] = new TypedDecisionExpectation { NoulAtLeast = 0.6 }
                },
                new Dictionary<string, TypedDecisionExpectation>(StringComparer.Ordinal)
                {
                    ["cognitive_complexity_weak"] = new TypedDecisionExpectation { NoulAtMost = 0.4 }
                }));

            // Consistency: the same clear change, reformatted, reads the same on readability.
            ChangeQualityInput clearA = new ChangeQualityInput
            {
                UnifiedDiff = "diff --git a/src/Greeter.cs b/src/Greeter.cs\n--- a/src/Greeter.cs\n+++ b/src/Greeter.cs\n@@ -1,1 +1,4 @@\n"
                    + "+    public string Greeting(string name)\n+    {\n+        return \"Hello, \" + name + \"!\";\n+    }\n"
            };
            ChangeQualityInput clearB = new ChangeQualityInput
            {
                UnifiedDiff = "diff --git a/src/Greeter.cs b/src/Greeter.cs\n--- a/src/Greeter.cs\n+++ b/src/Greeter.cs\n@@ -1,1 +1,2 @@\n"
                    + "+    public string Greeting(string name) => \"Hello, \" + name + \"!\";\n"
            };
            cases.Add(Consistency(
                "change_quality.formatting_does_not_change_readability",
                "change_quality",
                "A one-line method and its block form are equally readable.",
                adapter.DescribeRequest(clearA),
                adapter.DescribeRequest(clearB),
                "readability_weak"));
        }

        private static Mission EvalMission(string title, string persona)
        {
            return new Mission { Id = "msn_eval", VesselId = "vsl_eval", Title = title, Persona = persona };
        }

        private static TypedDecisionEvalCase Pair(
            string id,
            string decisionPoint,
            TypedDecisionEvalCaseKindEnum kind,
            string note,
            TypedDecisionBatchItem? variantA,
            TypedDecisionBatchItem? variantB,
            Dictionary<string, TypedDecisionExpectation> expectedA,
            Dictionary<string, TypedDecisionExpectation> expectedB)
        {
            return new TypedDecisionEvalCase
            {
                Id = id,
                DecisionPoint = decisionPoint,
                Kind = kind,
                Note = note,
                VariantA = variantA ?? throw new InvalidOperationException("Case " + id + " could not build variant A."),
                VariantB = variantB ?? throw new InvalidOperationException("Case " + id + " could not build variant B."),
                ExpectedA = expectedA,
                ExpectedB = expectedB
            };
        }

        private static TypedDecisionEvalCase Consistency(
            string id,
            string decisionPoint,
            string note,
            TypedDecisionBatchItem? variantA,
            TypedDecisionBatchItem? variantB,
            params string[] questionIds)
        {
            return new TypedDecisionEvalCase
            {
                Id = id,
                DecisionPoint = decisionPoint,
                Kind = TypedDecisionEvalCaseKindEnum.Consistency,
                Note = note,
                VariantA = variantA ?? throw new InvalidOperationException("Case " + id + " could not build variant A."),
                VariantB = variantB ?? throw new InvalidOperationException("Case " + id + " could not build variant B."),
                ConsistentQuestionIds = questionIds
            };
        }

        private static Dictionary<string, TypedDecisionExpectation> Expect(string questionId, TypedDecisionExpectation expectation)
        {
            return new Dictionary<string, TypedDecisionExpectation>(StringComparer.Ordinal) { [questionId] = expectation };
        }

        private static Dictionary<string, TypedDecisionExpectation> Expect(
            string firstId,
            TypedDecisionExpectation first,
            string secondId,
            TypedDecisionExpectation second)
        {
            return new Dictionary<string, TypedDecisionExpectation>(StringComparer.Ordinal) { [firstId] = first, [secondId] = second };
        }

        #endregion
    }
}
