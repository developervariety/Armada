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
        }
    }
}
