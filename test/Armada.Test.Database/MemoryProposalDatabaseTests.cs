namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Provider-backed memory proposal store proof: the migration is recorded, proposals round-trip
    /// every column across a reopen, the state filter and newest-first order hold, and dismissal is
    /// conditional on the Open state.
    /// </summary>
    internal sealed class MemoryProposalDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;

        internal MemoryProposalDatabaseTests(DatabaseDriver driver, DatabaseSettings settings)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 104, DatabaseTypeEnum.Postgresql => 105,
                DatabaseTypeEnum.Mysql => 96, DatabaseTypeEnum.SqlServer => 99,
                _ => throw new NotSupportedException()
            };
            Dictionary<int, string> history = await new MigrationScenarioRunner(_Settings).ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(history.ContainsKey(version), "The memory proposal migration is recorded");

            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string tenant = "ten_mpr_" + suffix;
            DateTime baseUtc = new DateTime(2033, 3, 4, 5, 6, 7, DateTimeKind.Utc);

            MemoryProposal older = await _Driver.MemoryProposals.CreateAsync(new MemoryProposal
            {
                TenantId = tenant,
                UserId = "usr_mpr",
                Source = MemoryProposal.SourcePapercutSweep,
                SourceKey = new string('a', 60) + suffix.Substring(0, 4),
                Title = "Gate a build on its log file, not a pipe — ünïcode kept",
                Body = "A pipeline reports the last command's status.",
                TargetHint = "shared",
                Confidence = 0.91,
                RelatedRecordIds = new List<string> { "msn_a_" + suffix, "msn_b_" + suffix },
                CreatedUtc = baseUtc,
                LastUpdateUtc = baseUtc
            }, token).ConfigureAwait(false);
            MemoryProposal newer = await _Driver.MemoryProposals.CreateAsync(new MemoryProposal
            {
                TenantId = tenant,
                Source = MemoryProposal.SourceRecorderSeam,
                SourceKey = new string('b', 60) + suffix.Substring(0, 4),
                Title = "A second lesson",
                TargetHint = "repos/<repo>",
                Confidence = 0.97,
                CreatedUtc = baseUtc.AddMinutes(1),
                LastUpdateUtc = baseUtc.AddMinutes(1)
            }, token).ConfigureAwait(false);

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                List<MemoryProposal> open = await reopened.MemoryProposals.EnumerateAsync(tenant, MemoryProposalStateEnum.Open, 10, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2, open.Count, "Both proposals are open in the tenant");
                DatabaseAssert.Equal(newer.Id, open[0].Id, "Proposals list newest first");

                MemoryProposal? read = await reopened.MemoryProposals.ReadAsync(older.Id, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(read, "The proposal reads back after reopen");
                DatabaseAssert.Equal("usr_mpr", read!.UserId, "User round-trips");
                DatabaseAssert.Equal("papercut_sweep", read.Source, "Source round-trips");
                DatabaseAssert.Equal(older.SourceKey, read.SourceKey, "Source key round-trips");
                DatabaseAssert.Equal(older.Title, read.Title, "Unicode title round-trips");
                DatabaseAssert.Equal(older.Body, read.Body, "Body round-trips");
                DatabaseAssert.Equal("shared", read.TargetHint, "Target hint round-trips");
                DatabaseAssert.True(Math.Abs(read.Confidence - 0.91) < 0.0001, "Confidence round-trips");
                DatabaseAssert.Equal(2, read.RelatedRecordIds.Count, "Related record ids round-trip");
                DatabaseAssert.Equal(baseUtc, read.CreatedUtc, "Created time round-trips");
                DatabaseAssert.True(read.DismissedUtc == null, "An open proposal has no dismissal time");

                MemoryProposal? byKey = await reopened.MemoryProposals.ReadBySourceKeyAsync(newer.SourceKey, token).ConfigureAwait(false);
                DatabaseAssert.Equal(newer.Id, byKey?.Id, "The proposal reads back by source key");

                DateTime dismissedUtc = baseUtc.AddHours(2);
                DatabaseAssert.True(await reopened.MemoryProposals.DismissAsync(older.Id, "operator-a", "promoted", dismissedUtc, token).ConfigureAwait(false), "An open proposal is dismissed");
                DatabaseAssert.True(!await reopened.MemoryProposals.DismissAsync(older.Id, "operator-b", "again", dismissedUtc.AddHours(1), token).ConfigureAwait(false), "A dismissed proposal is not dismissed again");

                MemoryProposal? dismissed = await reopened.MemoryProposals.ReadAsync(older.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(MemoryProposalStateEnum.Dismissed, dismissed!.State, "State is Dismissed");
                DatabaseAssert.Equal("operator-a", dismissed.DismissedBy, "The first operator is kept");
                DatabaseAssert.Equal("promoted", dismissed.DismissedReason, "The reason is kept");
                DatabaseAssert.Equal(dismissedUtc, dismissed.DismissedUtc, "The dismissal time round-trips");

                DatabaseAssert.Equal(1, (await reopened.MemoryProposals.EnumerateAsync(tenant, MemoryProposalStateEnum.Dismissed, 10, token).ConfigureAwait(false)).Count, "The state filter finds the dismissed proposal");
                DatabaseAssert.Equal(1, (await reopened.MemoryProposals.EnumerateAsync(tenant, MemoryProposalStateEnum.Open, 10, token).ConfigureAwait(false)).Count, "One proposal stays open");
                DatabaseAssert.Equal(1, (await reopened.MemoryProposals.EnumerateAsync(tenant, null, 1, token).ConfigureAwait(false)).Count, "The limit bounds the listing");
            }
        }
    }
}
