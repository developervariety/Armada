namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Database.Postgresql;
    using Armada.Test.Common;

    /// <summary>
    /// Verifies that PostgreSQL timestamp reads return the stored UTC instant whatever the host time zone,
    /// for TEXT columns written in either the ISO 8601 form or PostgreSQL's own text form.
    /// </summary>
    public sealed class PostgresqlUtcReadTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "PostgreSQL UTC Reads";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ReadUtc_TextAndTimestampValues_KeepTheUtcInstantUnderANonUtcHostZone", () =>
            {
                string? previousZone = Environment.GetEnvironmentVariable("TZ");
                Environment.SetEnvironmentVariable("TZ", "Asia/Tokyo");
                TimeZoneInfo.ClearCachedData();
                try
                {
                    AssertTrue(TimeZoneInfo.Local.BaseUtcOffset != TimeSpan.Zero,
                        "the test must run with a non-UTC local zone, got " + TimeZoneInfo.Local.Id);

                    DateTime expected = new DateTime(2026, 8, 20, 12, 34, 56, DateTimeKind.Utc);

                    DateTime postgresText = PostgresqlDatabaseDriver.ReadUtc("2026-08-20 12:34:56+00");
                    AssertEqual(expected, postgresText, "PostgreSQL text form with an offset reads as the same instant");
                    AssertEqual(DateTimeKind.Utc, postgresText.Kind, "PostgreSQL text form reads as UTC");

                    DateTime isoText = PostgresqlDatabaseDriver.ReadUtc("2026-08-20T12:34:56.0000000Z");
                    AssertEqual(expected, isoText, "ISO 8601 text reads as the same instant");
                    AssertEqual(DateTimeKind.Utc, isoText.Kind, "ISO 8601 text reads as UTC");

                    DateTime noOffsetText = PostgresqlDatabaseDriver.ReadUtc("2026-08-20 12:34:56");
                    AssertEqual(expected, noOffsetText, "text without an offset is read as UTC, not local time");

                    DateTime timestampColumn = PostgresqlDatabaseDriver.ReadUtc(new DateTime(2026, 8, 20, 12, 34, 56, DateTimeKind.Unspecified));
                    AssertEqual(expected, timestampColumn, "a TIMESTAMP column value keeps its UTC wall time");

                    DateTime? nullableText = PostgresqlDatabaseDriver.ReadUtcNullable("2026-08-20 12:34:56+00");
                    AssertEqual(expected, nullableText!.Value, "the optional read applies the same rule");
                    AssertTrue(PostgresqlDatabaseDriver.ReadUtcNullable(DBNull.Value) == null, "a null column reads as null");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("TZ", previousZone);
                    TimeZoneInfo.ClearCachedData();
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
