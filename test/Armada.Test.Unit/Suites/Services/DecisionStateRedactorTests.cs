namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text;
    using System.Threading.Tasks;
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
                // A decoder class name and a PGN name are product identifiers, not secrets: they
                // must pass through untouched so the model sees the engineering content.
                string text = "The J1939ParameterDecoder and PGN65259Decoder handle the DM20 record counter.";

                string redacted = DecisionStateRedactor.Redact(text, 8000);

                AssertContains("J1939ParameterDecoder", redacted);
                AssertContains("PGN65259Decoder", redacted);
                AssertContains("DM20", redacted);
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

            await RunTest("RedactObject_WalksSerializedStringFields_NoSecretSurvives", () =>
            {
                object state = new
                {
                    FailureReason = "captain died at /home/user/work",
                    Commit = "deadbeefdeadbeefdeadbeef0011",
                    Mission = "msn_example0002zz",
                    DecoderClass = "PGN64965Decoder"
                };

                object redactedObj = DecisionStateRedactor.RedactObject(state, 8000);
                string redacted = redactedObj as string ?? String.Empty;

                AssertFalse(redacted.Contains("/home/user", StringComparison.Ordinal), "path in object survived");
                AssertFalse(redacted.Contains("deadbeefdeadbeefdeadbeef0011", StringComparison.Ordinal), "sha in object survived");
                AssertFalse(redacted.Contains("msn_example0002zz", StringComparison.Ordinal), "id in object survived");
                // Product identifier still passes.
                AssertContains("PGN64965Decoder", redacted);
            });

            await RunTest("RedactObject_Null_ReturnsEmptyString", () =>
            {
                object result = DecisionStateRedactor.RedactObject(null, 8000);
                AssertEqual(String.Empty, result as string);
            });
        }
    }
}
