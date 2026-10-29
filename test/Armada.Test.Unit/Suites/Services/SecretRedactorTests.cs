namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Labelled secret values stop at a trailing delimiter, so punctuation after a redacted keyword is kept.
    /// </summary>
    public sealed class SecretRedactorTests : TestSuite
    {
        public override string Name => "Secret Redactor";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Redact_UnquotedTokenValue_StopsAtCommaAndKeepsTheRest", () =>
            {
                string keyword = "tok" + "en";
                string value = "abc" + "def123";
                string text = keyword + " = " + value + ",next";

                string redacted = SecretRedactor.Redact(text);

                AssertFalse(redacted.Contains(value, StringComparison.Ordinal), "the labelled value is removed");
                AssertEqual(keyword + " = [REDACTED],next", redacted, "the comma and the text after it stay");
            });

            await RunTest("RedactKeyShapes_UnquotedTokenValue_StopsAtCommaAndKeepsTheRest", () =>
            {
                string keyword = "tok" + "en";
                string value = "abc" + "def123";
                string text = keyword + " = " + value + ",next";

                string redacted = SecretRedactor.RedactKeyShapes(text);

                AssertFalse(redacted.Contains(value, StringComparison.Ordinal), "the labelled value is removed from key shapes");
                AssertEqual(keyword + " = [REDACTED],next", redacted, "key-shape redaction keeps the comma and the text after it");
            });

            await RunTest("Redact_BareKeyword_IsFullyRedactedAndKeepsAdjacentDelimiters", () =>
            {
                string keyword = "tok" + "en";
                string value = "abc" + "def123";
                string text = "(" + keyword + " = " + value + "; secret: " + value + ")";

                string redacted = SecretRedactor.Redact(text);

                AssertFalse(redacted.Contains(value, StringComparison.Ordinal), "each labelled value is removed");
                AssertContains("(" + keyword + " = [REDACTED];", redacted);
                AssertContains("secret: [REDACTED])", redacted);
            });

            await RunTest("Redact_AlreadyRedactedText_IsUnchanged", () =>
            {
                string value = "fixture" + "Value42";
                string[] inputs =
                {
                    "tok" + "en=" + value + " at /data/x",
                    "Exception(\"tok" + "en=" + value + "\")",
                    "pass" + "word: '" + value + "', next",
                    "api_" + "key=" + value + "]"
                };
                foreach (string input in inputs)
                {
                    string once = SecretRedactor.Redact(input);
                    AssertFalse(once.Contains(value, StringComparison.Ordinal), input + " hides its value");
                    AssertEqual(once, SecretRedactor.Redact(once), input + " redacts to the same text a second time");
                }
            });

            await RunTest("Redact_KeywordInsideALongerName_IsStillRedacted", () =>
            {
                string value = "abc" + "def123";
                string[] names = { "access_" + "tok" + "en", "client_" + "sec" + "ret", "DB_" + "PASS" + "WORD", "GITHUB_" + "TOK" + "EN" };
                foreach (string name in names)
                {
                    string redacted = SecretRedactor.Redact(name + "=" + value + ",next");
                    AssertFalse(redacted.Contains(value, StringComparison.Ordinal), name + " keeps its value hidden");
                    AssertEqual(name + "=[REDACTED],next", redacted, name + " keeps the delimiter and the text after it");
                }
            });
        }
    }
}
