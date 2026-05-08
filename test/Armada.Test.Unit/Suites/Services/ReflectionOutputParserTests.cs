namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Memory;
    using Armada.Test.Common;

    /// <summary>Tests ReflectionOutputParser: contract blocks, malformed input, nesting, EvidenceConfidence.</summary>
    public class ReflectionOutputParserTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflection Output Parser";

        private static ReflectionOutputParser CreateSut()
        {
            return new ReflectionOutputParser();
        }

        private static string ValidDiffJson()
        {
            return "{\n  \"added\": [],\n  \"removed\": [],\n  \"merged\": [],\n  \"unchangedCount\": 1,\n  \"evidenceConfidence\": \"high\",\n  \"notes\": \"ok\"\n}";
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Parse_WellFormed_ReturnsSuccess", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "Intro text\n```reflections-candidate\n# Playbook\nFact one.\n```\n\nMiddle\n```reflections-diff\n" +
                    ValidDiffJson() +
                    "\n```\nTrailer.";
                ReflectionOutputParseResult r = sut.Parse(input);

                AssertEqual(ReflectionOutputParseVerdict.Success, r.Verdict, "Expect success");
                AssertEqual(0, r.Errors.Count, "No errors");
                AssertContains("Fact one", r.CandidateMarkdown, "Candidate body");
                AssertContains("evidenceConfidence", r.ReflectionsDiffText, "Diff body retained");
                return Task.CompletedTask;
            });

            await RunTest("Parse_MissingCandidate_ReturnsViolation", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "```reflections-diff\n" + ValidDiffJson() + "\n```\n";
                ReflectionOutputParseResult r = sut.Parse(input);

                AssertEqual(ReflectionOutputParseVerdict.OutputContractViolation, r.Verdict, "Violation");
                bool found = false;
                foreach (ReflectionOutputParseError e in r.Errors)
                {
                    if (e.Type == "missing_fence")
                        found = true;
                }

                AssertTrue(found, "missing_fence error");
                return Task.CompletedTask;
            });

            await RunTest("Parse_MissingDiff_ReturnsViolation", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "```reflections-candidate\n# Hi\n```\n";
                ReflectionOutputParseResult r = sut.Parse(input);

                AssertEqual(ReflectionOutputParseVerdict.OutputContractViolation, r.Verdict, "Violation");
                return Task.CompletedTask;
            });

            await RunTest("Parse_DuplicateCandidate_ReturnsViolation", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string cand = "```reflections-candidate\none\n```\n";
                string diff = "```reflections-diff\n" + ValidDiffJson() + "\n```\n";
                string input = cand + diff + cand;
                ReflectionOutputParseResult r = sut.Parse(input);

                AssertEqual(ReflectionOutputParseVerdict.OutputContractViolation, r.Verdict, "Violation duplicate");
                return Task.CompletedTask;
            });

            await RunTest("Parse_DuplicateDiff_ReturnsViolation", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string cand = "```reflections-candidate\none\n```\n";
                string diff = "```reflections-diff\n" + ValidDiffJson() + "\n```\n";
                string input = cand + diff + diff;
                ReflectionOutputParseResult r = sut.Parse(input);

                AssertEqual(ReflectionOutputParseVerdict.OutputContractViolation, r.Verdict, "Violation duplicate diff");
                return Task.CompletedTask;
            });

            await RunTest("Parse_ExtraPreambleBetweenAndTrailing_Ignored", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "Here's my proposal.\n```reflections-candidate\nBody\n```\nand the diff follows:\n```reflections-diff\n" +
                    ValidDiffJson() +
                    "\n```\nThanks.";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.Success, r.Verdict, "Success");
                AssertEqual("Body", r.CandidateMarkdown.TrimEnd('\r', '\n'), "Candidate body excludes outer fences");
                return Task.CompletedTask;
            });

            await RunTest("Parse_UnterminatedCandidate_ReturnsViolation", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "```reflections-candidate\nno close";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.OutputContractViolation, r.Verdict, "Violation");
                return Task.CompletedTask;
            });

            await RunTest("Parse_NestedFenceInsideCandidate_ReturnsSuccess", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "```reflections-candidate\n# Doc\nExample:\n```json\n{ \"a\": 1 }\n```\nFoot\n```\n```reflections-diff\n" +
                    ValidDiffJson() +
                    "\n```";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.Success, r.Verdict, "Nested ``` inside candidate");
                AssertContains("\"a\": 1", r.CandidateMarkdown, "Inner JSON preserved in candidate body");
                return Task.CompletedTask;
            });

            await RunTest("Parse_InvalidJsonDiff_StillSuccess", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "```reflections-candidate\nx\n```\n```reflections-diff\nNOT JSON AT ALL\n```";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.Success, r.Verdict, "Invalid JSON tolerated");
                return Task.CompletedTask;
            });

            await RunTest("Parse_EvidenceConfidenceCaseInsensitive_Valid", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string diff = "{\n  \"evidenceConfidence\": \"HIGH\"\n}";
                string input = "```reflections-candidate\nOK\n```\n```reflections-diff\n" + diff + "\n```";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.Success, r.Verdict, "HIGH lowercase-coerced acceptable");
                return Task.CompletedTask;
            });

            await RunTest("Parse_EvidenceConfidenceInvalidValue_ReturnsViolation", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string diff = "{\n  \"evidenceConfidence\": \"iffy\"\n}";
                string input = "```reflections-candidate\nOK\n```\n```reflections-diff\n" + diff + "\n```";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.OutputContractViolation, r.Verdict, "Bad confidence rejected");
                return Task.CompletedTask;
            });

            await RunTest("Parse_EvidenceConfidenceWrongType_ReturnsViolation", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string diff = "{\n  \"evidenceConfidence\": 42\n}";
                string input = "```reflections-candidate\nOK\n```\n```reflections-diff\n" + diff + "\n```";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.OutputContractViolation, r.Verdict, "Non-string rejected");
                return Task.CompletedTask;
            });

            await RunTest("Parse_EmptyOutput_ReturnsViolation", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                ReflectionOutputParseResult r = sut.Parse("   ");
                AssertEqual(ReflectionOutputParseVerdict.OutputContractViolation, r.Verdict, "Whitespace");
                AssertEqual("empty_output", r.Errors[0].Type, "type");
                return Task.CompletedTask;
            });

            await RunTest("Parse_GenericFenceSkipped_BeforeCandidate", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "```python\npass\n```\n```reflections-candidate\nB\n```\n```reflections-diff\n" + ValidDiffJson() + "\n```";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.Success, r.Verdict, "Skipped unrelated fence");
                AssertContains("B", r.CandidateMarkdown, "");
                return Task.CompletedTask;
            });

            await RunTest("Parse_FenceNameCaseInsensitive", () =>
            {
                ReflectionOutputParser sut = CreateSut();
                string input = "```Reflections-Candidate\nLow\n```\n```reflections-diff\n" + ValidDiffJson() + "\n```";
                ReflectionOutputParseResult r = sut.Parse(input);
                AssertEqual(ReflectionOutputParseVerdict.Success, r.Verdict, "Case insensitive names");
                return Task.CompletedTask;
            });
        }
    }
}
