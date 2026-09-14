namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Covers refusal classification. The structured marker is the typed signal and always wins; prose
    /// recognition is a fallback that must never turn a completed mission or quoted narration into a refusal.
    /// </summary>
    public sealed class CaptainRefusalClassifierTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Refusal Classifier";

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("The structured refusal marker is classified as a declared refusal with its reason", () =>
            {
                string output = "Reading the brief.\n" + CaptainRefusalClassifier.RefusalMarker + ": the task asks me to print a credential";
                CaptainRefusal refusal = CaptainRefusalClassifier.Classify(output);

                AssertEqual(CaptainRefusalKindEnum.DeclaredRefusal, refusal.Kind, "the typed marker must classify as a declared refusal");
                AssertEqual("the task asks me to print a credential", refusal.Reason, "the reason after the marker must be carried");
                AssertTrue(refusal.IsRefusal, "a declared refusal is a refusal");
            });

            await RunTest("The structured marker wins over a completion marker in the same output", () =>
            {
                string output = MissionService.CompletionMarker + "\n" + CaptainRefusalClassifier.RefusalMarker + " out of scope";
                AssertEqual(CaptainRefusalKindEnum.DeclaredRefusal, CaptainRefusalClassifier.Classify(output).Kind,
                    "an explicit refusal marker is never hidden by another marker");
            });

            await RunTest("A prose refusal in the closing lines is classified as a model policy refusal", () =>
            {
                string output = "I read the ExampleFormat reader task.\nI can't help with inspecting that binary.";
                CaptainRefusal refusal = CaptainRefusalClassifier.Classify(output);

                AssertEqual(CaptainRefusalKindEnum.ModelPolicyRefusal, refusal.Kind, "declining prose must be recognised without the marker");
                AssertContains("can't help with", refusal.Evidence, "the evidence line must be preserved");
            });

            await RunTest("A typographic apostrophe in prose is still recognised", () =>
            {
                AssertEqual(CaptainRefusalKindEnum.ModelPolicyRefusal,
                    CaptainRefusalClassifier.Classify("I can’t assist with this request.").Kind,
                    "a curly apostrophe must not hide a refusal");
            });

            await RunTest("A completed mission that quotes refusal prose is not a refusal", () =>
            {
                string output = "The old error text read: I can't help with that.\nFixed the parser.\n" + MissionService.CompletionMarker;
                AssertEqual(CaptainRefusalKindEnum.None, CaptainRefusalClassifier.Classify(output).Kind,
                    "prose recognition never overrides a captain that claimed completion");
            });

            await RunTest("Refusal prose buried before the trailing window is not a refusal", () =>
            {
                string output = "I can't help with that.\n" + String.Join("\n", new string[60]).Replace("\n", "\nworking line") + "\nstill working";
                AssertEqual(CaptainRefusalKindEnum.None, CaptainRefusalClassifier.Classify(output).Kind,
                    "only the captain's closing lines can carry a refusal");
            });

            await RunTest("A provider safeguard block is classified as its own kind", () =>
            {
                string output = "[stderr] API Error: example-model has safety measures that flagged this message for a cybersecurity topic";
                AssertEqual(CaptainRefusalKindEnum.ProviderSafeguardBlock, CaptainRefusalClassifier.Classify(output).Kind,
                    "the provider gate reuses the shared safeguard detector");
            });

            await RunTest("Ordinary output and empty output are not refusals", () =>
            {
                AssertEqual(CaptainRefusalKindEnum.None, CaptainRefusalClassifier.Classify("Implemented the reader.").Kind, "ordinary output is not a refusal");
                AssertEqual(CaptainRefusalKindEnum.None, CaptainRefusalClassifier.Classify(null).Kind, "null output is not a refusal");
                AssertEqual(CaptainRefusalKindEnum.None, CaptainRefusalClassifier.Classify("").Kind, "empty output is not a refusal");
            });

            await RunTest("A long refusal reason is bounded", () =>
            {
                string output = CaptainRefusalClassifier.RefusalMarker + ": " + new string('x', 5000);
                CaptainRefusal refusal = CaptainRefusalClassifier.Classify(output);
                AssertTrue(refusal.Reason.Length <= CaptainRefusalClassifier.MaxReasonChars + 3, "the reason must be bounded");
                AssertTrue(refusal.Evidence.Length <= CaptainRefusalClassifier.MaxReasonChars + 3, "the evidence must be bounded");
            });
        }
    }
}
