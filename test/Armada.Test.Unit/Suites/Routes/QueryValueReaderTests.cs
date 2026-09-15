namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Threading.Tasks;
    using Armada.Server.Routes;
    using Armada.Test.Common;

    /// <summary>
    /// Verifies the single decoding rule every REST route applies to query-string values.
    /// </summary>
    public sealed class QueryValueReaderTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Query Value Reader";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Decode_PercentEncodedTimestamp_ParsesAsTheSentInstant", () =>
            {
                string? decoded = QueryValueReader.Decode("2026-09-14T00%3A00%3A00.000Z");
                AssertEqual("2026-09-14T00:00:00.000Z", decoded, "encoded colons are decoded");
                AssertTrue(DateTime.TryParse(decoded, out DateTime parsed), "the decoded value parses");
                AssertEqual(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc), parsed.ToUniversalTime(), "the parsed instant is the sent one");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Decode_UnencodedValue_IsReturnedAsSent", () =>
            {
                AssertEqual("2026-09-14T00:00:00Z", QueryValueReader.Decode("2026-09-14T00:00:00Z"), "an unencoded timestamp is unchanged");
                AssertEqual("2026-09-14T00:00:00+00:00", QueryValueReader.Decode("2026-09-14T00:00:00+00:00"), "a plus in an unencoded offset stays a plus");
                AssertTrue(QueryValueReader.Decode(null) == null, "an absent value stays absent");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Decode_DecodesExactlyOnce_AndKeepsMalformedEscapes", () =>
            {
                AssertEqual("%41", QueryValueReader.Decode("%2541"), "an encoded percent is decoded once, not twice");
                AssertEqual("fixed bug", QueryValueReader.Decode("fixed%20bug"), "an encoded space is decoded");
                AssertEqual("50%", QueryValueReader.Decode("50%"), "a malformed escape is kept as sent");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
