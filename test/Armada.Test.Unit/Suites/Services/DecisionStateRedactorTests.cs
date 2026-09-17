namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    public class DecisionStateRedactorTests : TestSuite
    {
        public override string Name => "Decision State Redactor";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Redact_FixtureWithOneOfEachProtectedClass_NoneSurvives", () =>
            {
                // One instance of each protected class the redactor must remove.
                string fixture = String.Join("\n", new[]
                {
                    "mission msn_example0001abc failed on the dock",
                    "log path /srv/app/docks/sample/run.log and E:\\work\\build\\x.txt",
                    "fetched https://internal.example.com/secret and host db.internal.net at 10.20.30.40",
                    "landed commit 0123456789abcdef01234567 onto main",
                    "keys sk-live0123456789abcdef and ghp_ABCDEFghijkl012345 and glpat-xyz0123456789 leaked",
                    "blob AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJ=="
                });

                string redacted = DecisionStateRedactor.Redact(fixture, 8000);

                // No raw secret of any class survives.
                AssertFalse(redacted.Contains("msn_example0001abc", StringComparison.Ordinal), "Armada id survived");
                AssertFalse(redacted.Contains("/srv/app", StringComparison.Ordinal), "unix path survived");
                AssertFalse(redacted.Contains("E:\\work", StringComparison.Ordinal), "windows path survived");
                AssertFalse(redacted.Contains("internal.example.com", StringComparison.Ordinal), "url host survived");
                AssertFalse(redacted.Contains("db.internal.net", StringComparison.Ordinal), "hostname survived");
                AssertFalse(redacted.Contains("10.20.30.40", StringComparison.Ordinal), "ipv4 survived");
                AssertFalse(redacted.Contains("0123456789abcdef01234567", StringComparison.Ordinal), "commit sha survived");
                AssertFalse(redacted.Contains("sk-live0123456789abcdef", StringComparison.Ordinal), "sk key survived");
                AssertFalse(redacted.Contains("ghp_ABCDEFghijkl012345", StringComparison.Ordinal), "github token survived");
                AssertFalse(redacted.Contains("glpat-xyz0123456789", StringComparison.Ordinal), "gitlab token survived");
                AssertFalse(redacted.Contains("AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJ", StringComparison.Ordinal), "base64 blob survived");

                // The replacement markers are present.
                AssertContains("#id", redacted);
                AssertContains("<path>", redacted);
                AssertContains("<host>", redacted);
                AssertContains("#sha", redacted);
                AssertContains("<secret>", redacted);
            });

            await RunTest("Redact_ProductIdentifier_Survives", () =>
            {
                // A decoder class name and a frame name are product identifiers, not secrets: they
                // must pass through untouched so the model sees the engineering content.
                string text = "The FrameParameterDecoder and Frame65259Decoder handle the RC20 record counter.";

                string redacted = DecisionStateRedactor.Redact(text, 8000);

                AssertContains("FrameParameterDecoder", redacted);
                AssertContains("Frame65259Decoder", redacted);
                AssertContains("RC20", redacted);
            });

            await RunTest("Redact_LongState_TruncatesButKeepsArmadaMarkerLines", () =>
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 400; i++) sb.Append("filler head line ").Append(i).Append('\n');
                sb.Append("[ARMADA:RESULT] BLOCKED needs the owner ruling\n");
                for (int i = 0; i < 400; i++) sb.Append("filler tail line ").Append(i).Append('\n');
                string big = sb.ToString();

                AssertTrue(big.Length > 2000, "fixture must exceed the budget");
                string redacted = DecisionStateRedactor.Redact(big, 1000);

                AssertTrue(redacted.Length <= big.Length, "truncated result is not longer than input");
                AssertContains("[ARMADA:RESULT] BLOCKED", redacted);
                AssertContains("[state truncated]", redacted);
            });

            await RunTest("Redact_NullOrEmpty_ReturnsEmpty", () =>
            {
                AssertEqual(String.Empty, DecisionStateRedactor.Redact(null, 8000));
                AssertEqual(String.Empty, DecisionStateRedactor.Redact(String.Empty, 8000));
            });

            await RunTest("RedactState_Object_TransmitsJsonObjectWithRedactedStrings", () =>
            {
                object state = new
                {
                    FailureReason = "captain died at /home/user/work",
                    Commit = "deadbeefdeadbeefdeadbeef0011",
                    Mission = "msn_example0002zz",
                    DecoderClass = "Frame64965Decoder"
                };

                RedactedDecisionState result = DecisionStateRedactor.RedactState(state, 8000);
                string redacted = result.Text;

                AssertTrue(result.State is JsonObject, "an object state is transmitted as a JSON object, not a string");
                AssertEqual(redacted, ((JsonObject)result.State).ToJsonString(), "the text is exactly the transmitted JSON");
                AssertEqual("#id", ((JsonObject)result.State)["Mission"]!.GetValue<string>(), "field structure survives redaction");

                AssertFalse(redacted.Contains("/home/user", StringComparison.Ordinal), "path in object survived");
                AssertFalse(redacted.Contains("deadbeefdeadbeefdeadbeef0011", StringComparison.Ordinal), "sha in object survived");
                AssertFalse(redacted.Contains("msn_example0002zz", StringComparison.Ordinal), "id in object survived");
                // Product identifier still passes.
                AssertContains("Frame64965Decoder", redacted);
            });

            await RunTest("RedactState_Null_ReturnsEmptyString", () =>
            {
                RedactedDecisionState result = DecisionStateRedactor.RedactState(null, 8000);
                AssertEqual(String.Empty, result.State as string);
                AssertEqual(String.Empty, result.Text);
            });

            await RunTest("RedactState_String_StaysText", () =>
            {
                RedactedDecisionState result = DecisionStateRedactor.RedactState("failed at /home/user/x", 8000);
                AssertTrue(result.State is string, "a string state is transmitted as text");
                AssertEqual(result.Text, (string)result.State);
                AssertFalse(result.Text.Contains("/home/user", StringComparison.Ordinal), "path survived");
            });

            await RunTest("RedactState_OversizedObject_TruncatesLongestLeafAndStaysValidJson", () =>
            {
                StringBuilder tail = new StringBuilder();
                for (int i = 0; i < 400; i++) tail.Append("output line ").Append(i).Append('\n');
                tail.Append("[ARMADA:RESULT] BLOCKED needs the owner ruling\n");
                for (int i = 0; i < 400; i++) tail.Append("more output ").Append(i).Append('\n');
                Dictionary<string, object?> state = new Dictionary<string, object?>
                {
                    ["failure_reason"] = "build failed",
                    ["exit_code"] = 1,
                    ["agent_output_tail"] = tail.ToString()
                };

                RedactedDecisionState result = DecisionStateRedactor.RedactState(state, 2000);

                AssertTrue(result.State is JsonObject, "an oversized object still transmits as a JSON object");
                AssertTrue(result.Text.Length <= 2000, "the serialized state fits the budget: " + result.Text.Length);
                JsonObject parsed = JsonNode.Parse(result.Text)!.AsObject();
                AssertEqual("build failed", parsed["failure_reason"]!.GetValue<string>(), "short fields are untouched");
                AssertEqual(1, parsed["exit_code"]!.GetValue<int>(), "numbers keep their type");
                string cut = parsed["agent_output_tail"]!.GetValue<string>();
                AssertContains("[state truncated]", cut);
                AssertContains("[ARMADA:RESULT] BLOCKED", cut);
            });

            await RunTest("RedactState_PropertyNamesAreRedacted", () =>
            {
                Dictionary<string, string> state = new Dictionary<string, string>
                {
                    ["/home/user/private.txt"] = "changed",
                    ["msn_example0003zz"] = "failed"
                };

                RedactedDecisionState result = DecisionStateRedactor.RedactState(state, 8000);

                AssertFalse(result.Text.Contains("/home/user", StringComparison.Ordinal), "a path used as a key survived");
                AssertFalse(result.Text.Contains("msn_example0003zz", StringComparison.Ordinal), "an id used as a key survived");
                AssertEqual(2, ((JsonObject)result.State).Count, "keys that redact alike stay distinct");
            });

            await RunTest("RedactState_ObjectThatCannotFit_FallsBackToTruncatedText", () =>
            {
                List<int> numbers = new List<int>();
                for (int i = 0; i < 2000; i++) numbers.Add(i);

                RedactedDecisionState result = DecisionStateRedactor.RedactState(new Dictionary<string, object> { ["numbers"] = numbers }, 500);

                AssertTrue(result.State is string, "a state with no string to shorten falls back to text");
                AssertContains("[state truncated]", result.Text);
            });
        }
    }
}
