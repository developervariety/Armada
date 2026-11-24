namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Additional exact snapshot predicate used only by completion-owned saves.</summary>
    internal static class MissionCompletionWritePersistence
    {
        internal static string UnchangedSql(DatabaseTypeEnum provider) =>
            MissionAutomaticWorkPermissionPersistence.EqualSql("tenant_id", "@completion_tenant", provider)
            + " AND " + MissionAutomaticWorkPermissionPersistence.EqualSql("user_id", "@completion_user", provider)
            + " AND " + MissionAutomaticWorkPermissionPersistence.EqualSql("commit_hash", "@completion_commit", provider)
            + " AND ((started_utc IS NULL AND @completion_started IS NULL) OR started_utc=@completion_started)"
            + " AND last_update_utc=@completion_stamp";

        // Completion alone writes these fresh ordinary timestamps. Do not change launch identity,
        // already stored values, other writers, text storage or timestamps inside JSON payloads.
        internal static void NormalizeChangedTimestamps(DatabaseTypeEnum provider, Mission working, Mission expected)
        {
            if (provider != DatabaseTypeEnum.Postgresql && provider != DatabaseTypeEnum.Mysql) return;
            if (expected.AutomaticWorkPermission == null && !expected.AutomaticWorkPermissionInvalid) return;
            StoredValueBinder binder = StoredValueBinder.For(provider);
            working.CompletedUtc = Normalize("completed_utc", working.CompletedUtc, expected.CompletedUtc);
            working.ReviewRequestedUtc = Normalize("review_requested_utc", working.ReviewRequestedUtc, expected.ReviewRequestedUtc);

            DateTime? Normalize(string column, DateTime? value, DateTime? previous)
            {
                if (!value.HasValue || Nullable.Equals(value, previous)
                    || binder.TimestampStorage("missions", column) != StoredTimestampEnum.Timestamp) return value;
                DateTime instant = StoredValueBinder.UtcInstant(value.Value);
                long ticks = instant.Ticks;
                if (provider == DatabaseTypeEnum.Postgresql)
                {
                    // Npgsql 10.0.3 PgTimestamp.Encode/Decode use signed microseconds from 2000.
                    // Preserve the existing MinValue/MaxValue infinity sentinels.
                    if (ticks == DateTime.MinValue.Ticks || ticks == DateTime.MaxValue.Ticks) return instant;
                    const long postgresEpochTicks = 630822816000000000L;
                    ticks = postgresEpochTicks + ((ticks - postgresEpochTicks) / 10) * 10;
                }
                else
                {
                    // MySqlConnector 2.6.2 emits six fractional digits on both wire paths.
                    ticks -= ticks % 10;
                }
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        internal static void Bind(DbCommand command, DatabaseTypeEnum provider, Mission expected)
        {
            if (expected.AutomaticWorkPermissionWriteStamp == null)
                throw new ArgumentException("A loaded completion snapshot is required.", nameof(expected));
            StoredValueBinder.For(provider).For(command, "missions")
                .Text("@completion_tenant", "tenant_id", expected.TenantId)
                .Text("@completion_user", "user_id", expected.UserId)
                .Text("@completion_commit", "commit_hash", expected.CommitHash)
                .Utc("@completion_started", "started_utc", expected.StartedUtc);
            DbParameter stamp = command.CreateParameter(); stamp.ParameterName = "@completion_stamp";
            stamp.Value = expected.AutomaticWorkPermissionWriteStamp; command.Parameters.Add(stamp);
        }
    }
}
